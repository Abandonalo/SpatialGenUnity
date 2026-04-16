using System;

[Serializable]
public class RefinementResponse
{
    public string requestId = string.Empty;

    public string refinedImageBase64 = string.Empty;
    public string meshBase64 = string.Empty;

    public bool success;
    public string errorMessage = string.Empty;
}
