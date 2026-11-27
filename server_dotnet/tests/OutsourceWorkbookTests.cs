using System.IO.Compression;
using System.Xml.Linq;
using Yf.Api.Modules.Outsource;

namespace Yf.Api.Tests;

public sealed class OutsourceWorkbookTests
{
    [Fact]
    public void InternalExportNumbersTheSelectedRowsInTheirGivenOrder()
    {
        var rows = Worksheet(OutsourceWorkbook.Purpose.InternalExport);
        Assert.Equal(["序号", "工令", "发送状态"], rows[0]);
        Assert.Equal([("1", "WO-B"), ("2", "WO-A")], rows.Skip(1).Select(row => (row[0], row[1])));
    }

    [Fact]
    public void VendorExportNumbersTheSelectedRowsInTheirGivenOrder()
    {
        var rows = Worksheet(OutsourceWorkbook.Purpose.VendorExport);
        Assert.Equal(["序号", "工令"], rows[0]);
        Assert.Equal([("1", "WO-B"), ("2", "WO-A")], rows.Skip(1).Select(row => (row[0], row[1])));
    }

    [Fact]
    public void MailAttachmentKeepsThePersistentSerialNumbers()
    {
        var rows = Worksheet(OutsourceWorkbook.Purpose.MailAttachment);
        Assert.Equal(["序号", "工令"], rows[0]);
        Assert.Equal([("43", "WO-B"), ("7", "WO-A")], rows.Skip(1).Select(row => (row[0], row[1])));
    }

    private static string[][] Worksheet(OutsourceWorkbook.Purpose purpose)
    {
        // The export query has already filtered and ordered these rows.
        var items = new[] { Item(43, "WO-B"), Item(7, "WO-A") };
        var bytes = OutsourceWorkbook.Build(items, null, [new("workOrderNo", "工令", 12, x => x.WorkOrderNo)], purpose);
        using var workbook = new ZipArchive(new MemoryStream(bytes));
        using var sheet = workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var worksheet = XDocument.Load(sheet);
        var ns = worksheet.Root!.Name.Namespace;
        return worksheet.Descendants(ns + "row").Select(row => row.Elements(ns + "c").Select(cell => cell.Value).ToArray()).ToArray();
    }

    private static OutsourceItemResponse Item(uint serialNo, string workOrderNo) => new(
        Id: serialNo, SerialNo: serialNo, WorkOrderNo: workOrderNo, OutsourceDate: new(2026, 11, 26), VendorId: 1, VendorName: "厂商",
        Customer: null, MachineModel: null, Supervisor: null, MechanicalOwner: null, ElectricalOwner: null,
        WorkOrderIssuedOn: null, BomOn: null, DrawingReleasedOn: null, TypeCode: null, TypeId: null, TypeLabel: null,
        Quantity: null, OriginalDueDate: null, LatestDueDate: null, Remark: null, Status: "NORMAL", SendStatus: "UNSENT",
        MatchState: "", Closed: false, IpmsSyncedAt: null, UpdatedAt: default, ElectricalHandoff: false,
        ElectricalHandoffAt: null, ElectricalHandoffSigner: null);
}
