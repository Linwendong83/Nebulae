#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>

using Microsoft::WRL::ComPtr;
namespace {
struct Context {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> commands;
    ComPtr<ID3D11ComputeShader> shader;
    ComPtr<ID3D11Buffer> constants, vertices, arguments, readVertices, readArguments;
    ComPtr<ID3D11UnorderedAccessView> vertexView, argumentView;
    ComPtr<ID3D11Query> completed;
    uint32_t capacity = 0;
};

int Fail(HRESULT hr, const char* step, char* error, int length) {
    if (error && length > 0) snprintf(error, length, "%s (HRESULT 0x%08lx)", step, static_cast<unsigned long>(hr));
    return static_cast<int>(FAILED(hr) ? hr : E_FAIL);
}

HRESULT Buffer(Context& c, uint32_t count, uint32_t stride, ComPtr<ID3D11Buffer>& gpu,
    ComPtr<ID3D11UnorderedAccessView>& view, ComPtr<ID3D11Buffer>& readback) {
    D3D11_BUFFER_DESC desc = {};
    desc.ByteWidth = count * stride;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    desc.StructureByteStride = stride;
    HRESULT hr = c.device->CreateBuffer(&desc, nullptr, gpu.ReleaseAndGetAddressOf());
    if (FAILED(hr)) return hr;
    D3D11_UNORDERED_ACCESS_VIEW_DESC uav = {};
    uav.Format = DXGI_FORMAT_UNKNOWN;
    uav.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    uav.Buffer.NumElements = count;
    hr = c.device->CreateUnorderedAccessView(gpu.Get(), &uav, view.ReleaseAndGetAddressOf());
    if (FAILED(hr)) return hr;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    desc.StructureByteStride = 0;
    return c.device->CreateBuffer(&desc, nullptr, readback.ReleaseAndGetAddressOf());
}
}

extern "C" __declspec(dllexport) int __cdecl NebulaeShieldAbi() { return 1; }

extern "C" __declspec(dllexport) int __cdecl NebulaeShieldCreate(const void* shader, int shaderSize,
    void** result, char* adapterName, int adapterNameSize, char* error, int errorSize) {
    if (!result) return E_INVALIDARG;
    *result = nullptr;
    try {
        if (!shader || shaderSize < 32 || shaderSize > 1048576) return Fail(E_INVALIDARG, "Invalid shader", error, errorSize);
        auto c = std::make_unique<Context>();
        D3D_FEATURE_LEVEL requested = D3D_FEATURE_LEVEL_11_0, actual;
        // Hardware only. Never allow D3D WARP/llvmpipe to masquerade as the GPU backend.
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, &requested, 1,
            D3D11_SDK_VERSION, &c->device, &actual, &c->commands);
        if (FAILED(hr)) return Fail(hr, "D3D11CreateDevice(hardware)", error, errorSize);
        ComPtr<IDXGIDevice> dxgi;
        ComPtr<IDXGIAdapter> adapter;
        DXGI_ADAPTER_DESC desc = {};
        hr = c->device.As(&dxgi);
        if (SUCCEEDED(hr)) hr = dxgi->GetAdapter(&adapter);
        if (SUCCEEDED(hr)) hr = adapter->GetDesc(&desc);
        if (FAILED(hr)) return Fail(hr, "Get GPU adapter", error, errorSize);
        // Microsoft's software adapter and Mesa CPU Vulkan devices must use the explicit CPU path.
        if (desc.VendorId == 0x1414 || wcsstr(desc.Description, L"llvmpipe") || wcsstr(desc.Description, L"lavapipe"))
            return Fail(E_FAIL, "Software graphics adapter rejected", error, errorSize);
        if (adapterName && adapterNameSize > 0)
            WideCharToMultiByte(CP_UTF8, 0, desc.Description, -1, adapterName, adapterNameSize, nullptr, nullptr);
        hr = c->device->CreateComputeShader(shader, shaderSize, nullptr, &c->shader);
        if (FAILED(hr)) return Fail(hr, "Create original shield shader", error, errorSize);
        D3D11_BUFFER_DESC constants = {};
        constants.ByteWidth = 82 * 16;
        constants.Usage = D3D11_USAGE_DEFAULT;
        constants.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        hr = c->device->CreateBuffer(&constants, nullptr, &c->constants);
        if (FAILED(hr)) return Fail(hr, "Create constants", error, errorSize);
        hr = Buffer(*c, 10, 4, c->arguments, c->argumentView, c->readArguments);
        if (FAILED(hr)) return Fail(hr, "Create counters", error, errorSize);
        D3D11_QUERY_DESC query = { D3D11_QUERY_EVENT, 0 };
        hr = c->device->CreateQuery(&query, &c->completed);
        if (FAILED(hr)) return Fail(hr, "Create completion fence", error, errorSize);
        *result = c.release();
        return 0;
    } catch (...) { return Fail(E_OUTOFMEMORY, "Initialize shield backend", error, errorSize); }
}

extern "C" __declspec(dllexport) int __cdecl NebulaeShieldRun(void* handle, const float* source,
    int vertexCount, const float* generators, int generatorCount, float radius, float altitude, float scale, float blend,
    float* outputVertices, float* outputNormals, uint32_t* outputArgs, char* error, int errorSize) {
    try {
        if (!handle || !source || !generators || !outputVertices || !outputNormals || !outputArgs ||
            vertexCount <= 0 || vertexCount > 65536 || generatorCount < 0 || generatorCount > 80)
            return Fail(E_INVALIDARG, "Invalid dispatch input", error, errorSize);
        auto& c = *static_cast<Context*>(handle);
        HRESULT hr = c.device->GetDeviceRemovedReason();
        if (FAILED(hr)) return Fail(hr, "GPU device removed", error, errorSize);
        if (c.capacity != static_cast<uint32_t>(vertexCount)) {
            ID3D11UnorderedAccessView* unbound[2] = {};
            c.commands->CSSetUnorderedAccessViews(0, 2, unbound, nullptr);
            hr = Buffer(c, vertexCount * 2, 12, c.vertices, c.vertexView, c.readVertices);
            if (FAILED(hr)) return Fail(hr, "Create vertex buffers", error, errorSize);
            c.capacity = vertexCount;
        }
        // Verified original DXBC constant layout: radius/altitude/count, 80 float4 generators,
        // then blend/vertexCount/physicsScale. Zero all padding and unused generator slots.
        uint8_t constants[82 * 16] = {};
        memcpy(constants + 0, &radius, 4);
        memcpy(constants + 4, &altitude, 4);
        memcpy(constants + 8, &generatorCount, 4);
        memcpy(constants + 16, generators, generatorCount * 16);
        memcpy(constants + 81 * 16, &blend, 4);
        memcpy(constants + 81 * 16 + 4, &vertexCount, 4);
        memcpy(constants + 81 * 16 + 8, &scale, 4);
        c.commands->UpdateSubresource(c.constants.Get(), 0, nullptr, constants, 0, 0);
        D3D11_BOX input = { 0, 0, 0, static_cast<UINT>(vertexCount * 12), 1, 1 };
        c.commands->UpdateSubresource(c.vertices.Get(), 0, &input, source, 0, 0);
        uint32_t zeros[10] = {};
        c.commands->UpdateSubresource(c.arguments.Get(), 0, nullptr, zeros, 0, 0);
        ID3D11UnorderedAccessView* views[] = { c.vertexView.Get(), c.argumentView.Get() };
        auto cb = c.constants.Get();
        c.commands->CSSetShader(c.shader.Get(), nullptr, 0);
        c.commands->CSSetConstantBuffers(0, 1, &cb);
        c.commands->CSSetUnorderedAccessViews(0, 2, views, nullptr);
        c.commands->Dispatch((vertexCount + 255) / 256, 1, 1);
        ID3D11UnorderedAccessView* unbound[2] = {};
        c.commands->CSSetUnorderedAccessViews(0, 2, unbound, nullptr);
        c.commands->CopyResource(c.readVertices.Get(), c.vertices.Get());
        c.commands->CopyResource(c.readArguments.Get(), c.arguments.Get());
        c.commands->End(c.completed.Get());
        c.commands->Flush();
        const auto deadline = GetTickCount64() + 5000;
        while ((hr = c.commands->GetData(c.completed.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH)) == S_FALSE) {
            if (GetTickCount64() >= deadline) return Fail(HRESULT_FROM_WIN32(WAIT_TIMEOUT), "Shield GPU timeout", error, errorSize);
            Sleep(0);
        }
        if (FAILED(hr)) return Fail(hr, "Shield GPU completion", error, errorSize);
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        hr = c.commands->Map(c.readVertices.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr)) return Fail(hr, "Read shield vertices", error, errorSize);
        memcpy(outputVertices, mapped.pData, vertexCount * 12);
        memcpy(outputNormals, static_cast<const uint8_t*>(mapped.pData) + vertexCount * 12, vertexCount * 12);
        c.commands->Unmap(c.readVertices.Get(), 0);
        hr = c.commands->Map(c.readArguments.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr)) return Fail(hr, "Read shield counters", error, errorSize);
        memcpy(outputArgs, mapped.pData, 40);
        c.commands->Unmap(c.readArguments.Get(), 0);
        return 0;
    } catch (...) { return Fail(E_OUTOFMEMORY, "Dispatch shield", error, errorSize); }
}

extern "C" __declspec(dllexport) void __cdecl NebulaeShieldDestroy(void* handle) {
    auto* c = static_cast<Context*>(handle);
    if (c && c->commands) c->commands->ClearState();
    delete c;
}
