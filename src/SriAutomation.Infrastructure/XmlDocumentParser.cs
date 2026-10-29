using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

public sealed class XmlDocumentParser : IDocumentParser
{
    public Voucher ParseXml(string account, byte[] xml)
    {
        Validation.Identifier(account);
        static XDocument Load(string content)
        {
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 });
            return XDocument.Load(reader);
        }
        var envelope = Load(System.Text.Encoding.UTF8.GetString(xml).TrimStart('\uFEFF'));
        var embedded = envelope.Descendants("comprobante").FirstOrDefault()?.Value;
        var document = embedded is null ? envelope : Load(embedded);
        var root = document.Root ?? throw new ArgumentException("XML sin documento.");
        var type = root.Name.LocalName switch
        {
            "factura" => "Facturas",
            "comprobanteRetencion" => "Retenciones",
            "notaCredito" => "Notas-Credito",
            "notaDebito" => "Notas-Debito",
            "liquidacionCompra" => "Liquidaciones",
            "guiaRemision" => "Guias-Remision",
            _ => throw new ArgumentException("Tipo de XML no soportado.")
        };
        var tax = root.Element("infoTributaria") ?? throw new ArgumentException("Falta infoTributaria.");
        var infoName = root.Name.LocalName switch
        {
            "factura" => "infoFactura",
            "comprobanteRetencion" => "infoCompRetencion",
            "notaCredito" => "infoNotaCredito",
            "notaDebito" => "infoNotaDebito",
            "liquidacionCompra" => "infoLiquidacionCompra",
            _ => "infoGuiaRemision"
        };
        var info = root.Element(infoName) ?? throw new ArgumentException("Falta información del comprobante.");
        string? Value(XElement parent, string name) => parent.Element(name)?.Value;
        decimal? Money(string? value) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount) ? amount : null;
        var date = Value(info, "fechaEmision");
        if (!DateOnly.TryParseExact(date, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued)) throw new ArgumentException("Fecha de emisión inválida.");
        var retention = type == "Retenciones";
        var recipient = Value(info, retention ? "razonSocialSujetoRetenido" : type == "Liquidaciones" ? "razonSocialProveedor" : "razonSocialComprador");
        var id = Value(info, retention ? "identificacionSujetoRetenido" : type == "Liquidaciones" ? "identificacionProveedor" : "identificacionComprador");
        if (type == "Guias-Remision")
        {
            var destinations = root.Element("destinatarios")?.Elements("destinatario").ToList() ?? [];
            if (destinations.Count == 1) { recipient = Value(destinations[0], "razonSocialDestinatario"); id = Value(destinations[0], "identificacionDestinatario"); }
        }
        var key = Validation.Identifier(Value(tax, "claveAcceso") ?? throw new ArgumentException("Falta clave de acceso."));
        var subtotal = Money(Value(info, "totalSinImpuestos"));
        decimal? taxes = null;
        var amounts = info.Element("totalConImpuestos")?.Elements("totalImpuesto").Select(i => Money(Value(i, "valor"))).ToList();
        if (amounts?.Count > 0 && amounts.All(v => v.HasValue)) taxes = amounts.Sum(v => v!.Value);
        var total = Money(Value(info, type == "Notas-Credito" ? "valorModificacion" : type == "Notas-Debito" ? "valorTotal" : "importeTotal"));
        if (retention)
        {
            var retained = root.Descendants("valorRetenido").Select(i => Money(i.Value)).ToList();
            total = retained.Count > 0 && retained.All(v => v.HasValue) ? retained.Sum(v => v!.Value) : null;
        }
        var warnings = new List<string>();
        if (recipient is null || id is null) warnings.Add("Receptor ausente o múltiple.");
        if (type is "Facturas" or "Liquidaciones" or "Notas-Credito" or "Notas-Debito")
        {
            if (subtotal is null) warnings.Add("Subtotal ausente o no interpretable.");
            if (taxes is null) warnings.Add("Impuestos ausentes o no interpretables.");
        }
        if (new[] { "estab", "ptoEmi", "secuencial" }.Any(name => string.IsNullOrWhiteSpace(Value(tax, name)))) warnings.Add("Número de comprobante incompleto.");
        if (type != "Guias-Remision" && total is null) warnings.Add("Importe no disponible.");
        if (subtotal.HasValue && taxes.HasValue && total.HasValue && type is "Facturas" or "Liquidaciones" && Math.Abs(subtotal.Value + taxes.Value - total.Value) > 0.01m)
            warnings.Add("Revisar conciliación: descuentos, propina u otros conceptos.");
        var authorized = envelope.Root?.Name.LocalName == "autorizacion";
        if (!authorized || Value(envelope.Root!, "estado") != "AUTORIZADO") warnings.Add("Autorización no verificada.");
        if (authorized && (string.IsNullOrWhiteSpace(Value(envelope.Root!, "numeroAutorizacion")) || string.IsNullOrWhiteSpace(Value(envelope.Root!, "fechaAutorizacion")))) warnings.Add("Datos de autorización incompletos.");
        if (key.Length != 49 || !key.All(char.IsAsciiDigit)) warnings.Add("Clave de acceso con formato no válido.");
        return new Voucher
        {
            Account = account,
            Type = type,
            AccessKey = key,
            Issued = issued,
            IssuerTaxId = Value(tax, "ruc"),
            Number = string.Join("-", new[] { Value(tax, "estab"), Value(tax, "ptoEmi"), Value(tax, "secuencial") }),
            Recipient = recipient,
            Identification = id,
            Subtotal = subtotal,
            Taxes = taxes,
            Total = total,
            Authorization = authorized ? Value(envelope.Root!, "numeroAutorizacion") : null,
            AuthorizedAt = authorized ? Value(envelope.Root!, "fechaAutorizacion") : null,
            Status = warnings.Count == 0 ? "ValidadoXML" : "Revisar",
            Warning = string.Join(" ", warnings)
        };
    }
}
