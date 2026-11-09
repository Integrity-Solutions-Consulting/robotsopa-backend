using System.Text;
using ClosedXML.Excel;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public sealed class MonthlyXmlReportTests
{
    [Fact]
    public void AccountingSheetsShowRepeatedTaxesAndPreserveSupportDocument()
    {
        var dir=Path.Combine(Path.GetTempPath(),"sri-accounting-"+Guid.NewGuid().ToString("N"));
        try
        {
            var xml="""
            <comprobanteRetencion version="2.0.0"><infoTributaria><claveAcceso>123</claveAcceso></infoTributaria><infoCompRetencion><fechaEmision>12/09/2026</fechaEmision><periodoFiscal>09/2026</periodoFiscal></infoCompRetencion>
            <docsSustento><docSustento><codDocSustento>42</codDocSustento><numDocSustento>001010000047432</numDocSustento><impuestosDocSustento><impuestoDocSustento><codImpuestoDocSustento>2</codImpuestoDocSustento><codigoPorcentaje>0000</codigoPorcentaje><baseImponible>127500.00</baseImponible><tarifa>0.00</tarifa><valorImpuesto>0.00</valorImpuesto></impuestoDocSustento></impuestosDocSustento><retenciones><retencion><codigo>1</codigo><codigoRetencion>3481</codigoRetencion><baseImponible>127500.00</baseImponible><porcentajeRetener>4.00</porcentajeRetener><valorRetenido>5100.00</valorRetenido></retencion><retencion><codigo>2</codigo><codigoRetencion>0001</codigoRetencion><baseImponible>50</baseImponible><porcentajeRetener>10</porcentajeRetener><valorRetenido>5</valorRetenido></retencion></retenciones></docSustento></docsSustento></comprobanteRetencion>
            """;
            var storage=new DocumentStorage(dir);var bytes=Encoding.UTF8.GetBytes(xml);var d=new XmlDocumentParser().ParseXml("account",bytes);d.XmlPath=storage.Save(d,"xml",bytes);
            using var book=new XLWorkbook(storage.Resolve(storage.Report("account",2026,9,[d])));var ws=book.Worksheet("Impuestos y retenciones");
            Assert.Equal(4,ws.LastRowUsed()!.RowNumber());Assert.Equal("Impuesto documento sustento",ws.Cell(2,3).GetString());Assert.Equal(0m,ws.Cell(2,11).GetValue<decimal>());
            Assert.Equal("001010000047432",ws.Cell(3,6).GetString());Assert.Equal("3481",ws.Cell(3,8).GetString());Assert.Equal(5100m,ws.Cell(3,12).GetValue<decimal>());Assert.Equal("0001",ws.Cell(4,8).GetString());
            Assert.Equal(5105m,book.Worksheet("Resumen").Cell(2,10).GetValue<decimal>());Assert.True(ws.AutoFilter.IsEnabled);
        }
        finally { if(Directory.Exists(dir)) Directory.Delete(dir,true); }
    }
    [Fact]
    public void MixedSampleWorkbookCanBeReopenedAndHasFormattedAccountingSheets()
    {
        var dir=Path.Combine(Path.GetTempPath(),"sri-sample-"+Guid.NewGuid().ToString("N"));
        try
        {
            var storage=new DocumentStorage(dir);var docs=new List<SriAutomation.Domain.Voucher>();
            foreach(var (root,info) in new[]{("factura","infoFactura"),("comprobanteRetencion","infoCompRetencion"),("notaCredito","infoNotaCredito"),("notaDebito","infoNotaDebito"),("liquidacionCompra","infoLiquidacionCompra")})
            {
                var totalTag=root=="notaCredito"?"valorModificacion":root=="notaDebito"?"valorTotal":"importeTotal";
                var repeated=root=="comprobanteRetencion"?"<impuestos><impuesto><codigo>1</codigo><codigoRetencion>332</codigoRetencion><baseImponible>10.00</baseImponible><porcentajeRetener>2.00</porcentajeRetener><valorRetenido>0.20</valorRetenido><codDocSustento>01</codDocSustento><numDocSustento>001001000000001</numDocSustento></impuesto></impuestos>":"<detalles><detalle><descripcion>Ejemplo ficticio</descripcion><cantidad>2</cantidad><precioUnitario>5</precioUnitario><descuento>0</descuento><precioTotalSinImpuesto>10</precioTotalSinImpuesto><impuestos><impuesto><codigo>2</codigo><codigoPorcentaje>4</codigoPorcentaje><tarifa>15</tarifa><baseImponible>10</baseImponible><valor>1.50</valor></impuesto></impuestos></detalle></detalles>";
                var xml=$"<{root} version='1.0.0'><infoTributaria><claveAcceso>{docs.Count+1:000000}</claveAcceso></infoTributaria><{info}><fechaEmision>12/09/2026</fechaEmision><totalSinImpuestos>10.00</totalSinImpuestos><{totalTag}>11.50</{totalTag}></{info}>{repeated}</{root}>";
                var bytes=Encoding.UTF8.GetBytes(xml);var d=new XmlDocumentParser().ParseXml("account",bytes);d.XmlPath=storage.Save(d,"xml",bytes);docs.Add(d);
            }
            var path=storage.Resolve(storage.Report("account",2026,9,docs));
            using(var book=new XLWorkbook(path))
            {
                foreach(var name in new[]{"Resumen","Facturas","Retenciones","Notas de crédito","Notas de débito","Liquidaciones de compra","Productos y motivos","Impuestos y retenciones"})Assert.True(book.Worksheet(name).AutoFilter.IsEnabled);
                Assert.Equal(XLDataType.DateTime,book.Worksheet("Resumen").Cell(2,4).DataType);Assert.Equal(XLDataType.Text,book.Worksheet("Resumen").Cell(2,3).DataType);
                Assert.Equal(0m,book.Worksheet("Productos y motivos").Cell(2,9).GetValue<decimal>());
            }
            var destination=Environment.GetEnvironmentVariable("SRI_REPORT_SAMPLE_PATH");
            if(!string.IsNullOrEmpty(destination)){Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(path,destination,true);}
        }
        finally { if(Directory.Exists(dir)) Directory.Delete(dir,true); }
    }
    [Theory]
    [InlineData("factura", "infoFactura", "Facturas", "Facturas", "importeTotal", "1.0.0")]
    [InlineData("factura", "infoFactura", "Facturas", "Facturas", "importeTotal", "1.1.0")]
    [InlineData("factura", "infoFactura", "Facturas", "Facturas", "importeTotal", "2.0.0")]
    [InlineData("factura", "infoFactura", "Facturas", "Facturas", "importeTotal", "2.1.0")]
    [InlineData("notaCredito", "infoNotaCredito", "Notas-Credito", "Notas de crédito", "valorModificacion", "1.0.0")]
    [InlineData("notaCredito", "infoNotaCredito", "Notas-Credito", "Notas de crédito", "valorModificacion", "1.1.0")]
    [InlineData("notaDebito", "infoNotaDebito", "Notas-Debito", "Notas de débito", "valorTotal", "1.0.0")]
    [InlineData("liquidacionCompra", "infoLiquidacionCompra", "Liquidaciones", "Liquidaciones de compra", "importeTotal", "1.0.0")]
    [InlineData("liquidacionCompra", "infoLiquidacionCompra", "Liquidaciones", "Liquidaciones de compra", "importeTotal", "1.1.0")]
    [InlineData("comprobanteRetencion", "infoCompRetencion", "Retenciones", "Retenciones", "importeTotal", "1.0.0")]
    [InlineData("comprobanteRetencion", "infoCompRetencion", "Retenciones", "Retenciones", "importeTotal", "2.0.0")]
    public void ReportReadsStoredXmlAndKeepsRepeatedAmounts(string root, string info, string type, string sheetName, string totalTag, string version)
    {
        var dir=Path.Combine(Path.GetTempPath(),"sri-report-"+Guid.NewGuid().ToString("N"));
        try
        {
            var xml=$"""
                <{root} version="{version}"><infoTributaria><ruc>1790000000001</ruc><claveAcceso>0109202601179000000000110010010000000011234567811</claveAcceso><estab>001</estab><ptoEmi>001</ptoEmi><secuencial>000000001</secuencial></infoTributaria>
                <{info}><fechaEmision>12/09/2026</fechaEmision><razonSocialComprador>Comprador</razonSocialComprador><identificacionComprador>0012345678</identificacionComprador><totalSinImpuestos>10.00</totalSinImpuestos><totalDescuento>0.00</totalDescuento><totalConImpuestos><totalImpuesto><valor>1.25</valor></totalImpuesto><totalImpuesto><valor>0.25</valor></totalImpuesto></totalConImpuestos><{totalTag}>11.50</{totalTag}></{info}>
                <detalles><detalle><descripcion>Uno</descripcion><descuento>2.00</descuento></detalle><detalle><descripcion>Dos</descripcion><descuento>0.00</descuento></detalle></detalles><impuestos><impuesto><valorRetenido>1.00</valorRetenido></impuesto><impuesto><valorRetenido>2.00</valorRetenido></impuesto></impuestos></{root}>
                """;
            var bytes=Encoding.UTF8.GetBytes(xml); var parser=new XmlDocumentParser(); var d=parser.ParseXml("account",bytes);
            Assert.Equal(type,d.Type);var storage=new DocumentStorage(dir);d.XmlPath=storage.Save(d,"xml",bytes);
            var original=File.ReadAllBytes(storage.Resolve(d.XmlPath));
            var path=storage.Report("account",2026,9,[d,d]);
            using(var book=new XLWorkbook(storage.Resolve(path)))
            {
                Assert.Equal(2,book.Worksheet("Resumen").LastRowUsed()!.RowNumber());
                Assert.False(book.Worksheets.Any(w => w.Name == "Detalle XML" || w.Name == "Documentos"));
                Assert.Equal(XLDataType.DateTime,book.Worksheet("Resumen").Cell(2,4).DataType);
                Assert.Equal(XLDataType.Text,book.Worksheet("Resumen").Cell(2,3).DataType);
                if(type=="Retenciones")Assert.Equal(3m,book.Worksheet("Resumen").Cell(2,10).GetValue<decimal>());
                else Assert.Equal(11.5m,book.Worksheet("Resumen").Cell(2,10).GetValue<decimal>());
            }
            Assert.Equal(original,File.ReadAllBytes(storage.Resolve(d.XmlPath)));
        }
        finally { if(Directory.Exists(dir)) Directory.Delete(dir,true); }
    }
    [Fact]
    public void MissingAndInvalidXmlAreReportedAndRegenerationUsesNewStoredXml()
    {
        var dir=Path.Combine(Path.GetTempPath(),"sri-report-"+Guid.NewGuid().ToString("N"));
        try
        {
            var xml="<factura version='1.0.0'><infoTributaria><claveAcceso>123</claveAcceso></infoTributaria><infoFactura><fechaEmision>12/09/2026</fechaEmision><importeTotal>0.00</importeTotal></infoFactura></factura>";
            var storage=new DocumentStorage(dir);var d=new XmlDocumentParser().ParseXml("account",Encoding.UTF8.GetBytes(xml)); d.Status = "ValidadoXML";
            var report=storage.Report("account",2026,9,[d]);
            using(var book=new XLWorkbook(storage.Resolve(report))) { Assert.Equal(14, book.Worksheet("Resumen").LastColumnUsed()!.ColumnNumber()); Assert.Equal("XML no verificable", book.Worksheet("Pendientes").Cell(2, 2).GetString()); }
            d.XmlPath=storage.Save(d,"xml",Encoding.UTF8.GetBytes(xml));storage.Report("account",2026,9,[d]);
            using(var book=new XLWorkbook(storage.Resolve(report))) {  Assert.Equal(0m,book.Worksheet("Resumen").Cell(2,10).GetValue<decimal>()); Assert.Equal("No consta en XML",book.Worksheet("Facturas").Cell(2,15).GetString()); Assert.Equal(1, book.Worksheet("Pendientes").LastRowUsed()!.RowNumber()); }
            File.WriteAllText(storage.Resolve(d.XmlPath),"<broken");storage.Report("account",2026,9,[d]);
            using(var book=new XLWorkbook(storage.Resolve(report))) Assert.Equal(14, book.Worksheet("Resumen").LastColumnUsed()!.ColumnNumber());
        }
        finally { if(Directory.Exists(dir)) Directory.Delete(dir,true); }
    }
    [Fact]
    public void AuthorizedXmlInContentAddressedStorageIsReadAndHashMismatchIsReported()
    {
        var dir=Path.Combine(Path.GetTempPath(),"sri-report-object-"+Guid.NewGuid().ToString("N"));
        try
        {
            var inner="<factura version='2.1.0'><infoTributaria><claveAcceso>123</claveAcceso></infoTributaria><infoFactura><fechaEmision>12/09/2026</fechaEmision><importeTotal>15.25</importeTotal></infoFactura></factura>";
            var bytes=Encoding.UTF8.GetBytes("<autorizacion><estado>AUTORIZADO</estado><numeroAutorizacion>000123</numeroAutorizacion><comprobante><![CDATA["+inner+"]]></comprobante></autorizacion>");
            var storage=new DocumentStorage(dir);var d=new XmlDocumentParser().ParseXml("account",bytes);
            d.XmlSha256=DownloadPipeline.Hash(bytes);d.XmlPath=Path.Combine(".objects",d.XmlSha256[..2],d.XmlSha256+".xml");
            var path=storage.Resolve(d.XmlPath);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);
            var report=storage.Report("account",2026,9,[d]);
            using(var book=new XLWorkbook(storage.Resolve(report))) { Assert.Equal("000123",book.Worksheet("Resumen").Cell(2,7).GetString()); }
            d.XmlSha256=new string('0',64);storage.Report("account",2026,9,[d]);
            using(var book=new XLWorkbook(storage.Resolve(report))) Assert.Equal("No disponible", book.Worksheet("Facturas").Cell(2,15).GetString());
            Assert.Equal(bytes,File.ReadAllBytes(path));
        }
        finally { if(Directory.Exists(dir)) Directory.Delete(dir,true); }
    }
}
