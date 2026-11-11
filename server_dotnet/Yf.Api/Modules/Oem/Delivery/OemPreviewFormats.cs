namespace Yf.Api.Modules.Oem.Delivery;

/// <summary>
/// Which stored files the browser can preview in-page. Keep in sync with <c>web/src/oem/components/oemPreview.ts</c>
/// (the UI picks the viewer from the same extensions); everything else is download-only.
/// </summary>
public static class OemPreviewFormats
{
    public static bool IsPreviewable(string? extension, ulong sizeBytes) =>
        sizeBytes <= (ulong)OemDeliveryService.PreviewLimitBytes
        && (extension ?? string.Empty).ToLowerInvariant() is
            "pdf" or "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp"
            or "xls" or "xlsx" or "xlsm" or "xlsb" or "pptx" or "docx";
}
