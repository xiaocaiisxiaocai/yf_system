namespace Yf.Api.Modules.Oem.Delivery;

/// <summary>
/// Which stored files the browser can preview in-page. Keep in sync with <c>web/src/oem/components/oemPreview.ts</c>
/// (the UI picks the viewer from the same extensions); everything else is download-only.
/// </summary>
public static class OemPreviewFormats
{
    public static bool IsPreviewableExtension(string? extension) =>
        NormalizeExtension(extension) is
            "pdf" or "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp"
            or "xls" or "xlsx" or "xlsm" or "xlsb" or "pptx" or "docx";

    public static bool IsPreviewable(string? extension, ulong sizeBytes) =>
        sizeBytes <= (ulong)OemDeliveryService.PreviewLimitBytes && IsPreviewableExtension(extension);

    public static string ContentTypeFor(string? extension) => NormalizeExtension(extension) switch
    {
        "pdf" => "application/pdf",
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "xls" => "application/vnd.ms-excel",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "xlsm" => "application/vnd.ms-excel.sheet.macroEnabled.12",
        "xlsb" => "application/vnd.ms-excel.sheet.binary.macroEnabled.12",
        "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    private static string NormalizeExtension(string? extension) =>
        (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
}
