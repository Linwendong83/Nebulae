#region

using System;
using UnityEngine.UI;

#endregion

namespace NebulaeModel.Attributes;

[AttributeUsage(AttributeTargets.Property)]
internal class UIContentTypeAttribute : Attribute
{
    public readonly InputField.ContentType ContentType;

    public UIContentTypeAttribute(InputField.ContentType contentType)
    {
        ContentType = contentType;
    }
}
