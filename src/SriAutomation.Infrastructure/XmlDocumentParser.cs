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
        using var stream = new MemoryStream(xml, writable: false);
        using var input = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 });
        var envelope = XDocument.Load(input);
        var embedded = envelope.Descendants().FirstOrDefault(e => e.Name.LocalName == "comprobante")?.Value;
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
        XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        var tax = Child(root, "infoTributaria") ?? throw new ArgumentException("Falta infoTributaria.");
        var infoName = root.Name.LocalName switch
        {
            "factura" => "infoFactura",
            "comprobanteRetencion" => "infoCompRetencion",
            "notaCredito" => "infoNotaCredito",
            "notaDebito" => "infoNotaDebito",
            "liquidacionCompra" => "infoLiquidacionCompra",
            _ => "infoGuiaRemision"
        };
        var info = Child(root, infoName) ?? throw new ArgumentException("Falta información del comprobante.");
        string? Value(XElement parent, string name) => Child(parent, name)?.Value;
        decimal? Money(string? value) => decimal.TryParse(value?.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount) ? amount : null;
        var key = Validation.Identifier(Value(tax, "claveAcceso") ?? throw new ArgumentException("Falta clave de acceso."));
        var date = Value(info, "fechaEmision");
        var fromKey = false;
        if (!DateOnly.TryParseExact(date?.Trim(), ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued))
        {
            // Guías may omit fechaEmision; the SRI access key encodes it in its first eight digits.
            if (type != "Guias-Remision" || !string.IsNullOrWhiteSpace(date) || key.Length != 49 || !key.All(char.IsAsciiDigit) ||
                !DateOnly.TryParseExact(key[..8], "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out issued))
                throw new ArgumentException("Fecha de emisión inválida.");
            fromKey = true;
        }
        var retention = type == "Retenciones";
        var recipient = Value(info, retention ? "razonSocialSujetoRetenido" : type == "Liquidaciones" ? "razonSocialProveedor" : "razonSocialComprador");
        var id = Value(info, retention ? "identificacionSujetoRetenido" : type == "Liquidaciones" ? "identificacionProveedor" : "identificacionComprador");
        if (type == "Guias-Remision")
        {
            var destinations = Child(root, "destinatarios")?.Elements().Where(e => e.Name.LocalName == "destinatario").ToList() ?? [];
            if (destinations.Count == 1) { recipient = Value(destinations[0], "razonSocialDestinatario"); id = Value(destinations[0], "identificacionDestinatario"); }
        }
        var subtotal = Money(Value(info, "totalSinImpuestos"));
        decimal? taxes = null;
        var amounts = Child(info, "totalConImpuestos")?.Elements().Where(e => e.Name.LocalName == "totalImpuesto").Select(i => Money(Value(i, "valor"))).ToList();
        if (type == "Notas-Debito")
            amounts = Child(info, "impuestos")?.Elements().Where(e => e.Name.LocalName == "impuesto").Select(i => Money(Value(i, "valor"))).ToList();
        if (amounts?.Count > 0 && amounts.All(v => v.HasValue)) taxes = amounts.Sum(v => v!.Value);
        var total = Money(Value(info, type == "Notas-Credito" ? "valorModificacion" : type == "Notas-Debito" ? "valorTotal" : "importeTotal"));
        if (retention)
        {
            var retained = root.Descendants().Where(e => e.Name.LocalName == "valorRetenido").Select(i => Money(i.Value)).ToList();
            total = retained.Count > 0 && retained.All(v => v.HasValue) ? retained.Sum(v => v!.Value) : null;
        }
        var warnings = new List<string>();
        if (fromKey) warnings.Add("Fecha de emisión obtenida de la clave de acceso; verificar clave.");
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
        var details = ExtractXmlData(root, authorized ? envelope.Root : null);
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
            Warning = string.Join(" ", warnings),
            XmlData = details
        };
    }

    private static VoucherXmlData ExtractXmlData(XElement root, XElement? authorization)
    {
        static string? ChildValue(XElement? element, string name) => element?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        static decimal? DecimalValue(string? value) => decimal.TryParse(value?.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result) ? result : null;
        static string PathOf(XElement element)
        {
            var segments = element.AncestorsAndSelf().Reverse().Select(e =>
                $"{e.Name.LocalName}[{1 + e.ElementsBeforeSelf().Count(s => s.Name.LocalName == e.Name.LocalName)}]");
            return string.Join("/", segments);
        }
        var data = new VoucherXmlData
        {
            Version = root.Attribute("version")?.Value,
            DocumentCode = ChildValue(root.Elements().FirstOrDefault(e => e.Name.LocalName == "infoTributaria"), "codDoc"),
            AuthorizationStatus = ChildValue(authorization, "estado")
        };
        foreach (var element in root.DescendantsAndSelf())
        {
            var path = PathOf(element);
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                data.Fields.Add(new(path + "/@" + attribute.Name.LocalName, attribute.Value));
            if (!element.HasElements) data.Fields.Add(new(path, element.Value));
            if (element.Name.LocalName == "detalle")
                data.Lines.Add(new(path, ChildValue(element, "codigoPrincipal") ?? ChildValue(element, "codigoInterno"), ChildValue(element, "descripcion"),
                    DecimalValue(ChildValue(element, "cantidad")), DecimalValue(ChildValue(element, "precioUnitario")),
                    DecimalValue(ChildValue(element, "descuento")), DecimalValue(ChildValue(element, "precioTotalSinImpuesto"))));
            if (element.Name.LocalName is "impuesto" or "totalImpuesto" && ChildValue(element, "valor") is not null)
                data.TaxLines.Add(new(path, ChildValue(element, "codigo"), ChildValue(element, "codigoPorcentaje"),
                    DecimalValue(ChildValue(element, "baseImponible")), DecimalValue(ChildValue(element, "tarifa")), DecimalValue(ChildValue(element, "valor"))));
            var relatedNumber = ChildValue(element, "numDocSustento") ?? ChildValue(element, "numDocModificado");
            if (relatedNumber is not null)
                data.RelatedDocuments.Add(new(path, ChildValue(element, "codDocSustento") ?? ChildValue(element, "codDocModificado"), relatedNumber,
                    ChildValue(element, "fechaEmisionDocSustento"), ChildValue(element, "numAutDocSustento")));
            if (element.Name.LocalName is "detalle" or "motivo" or "destinatario" or "totalImpuesto" or "impuesto" or "pago")
            {
                var values = element.Elements().Where(e => !e.HasElements)
                    .Select(e => new VoucherXmlField(e.Name.LocalName, e.Value)).ToList();
                if (values.Count > 0) data.Items.Add(new(element.Name.LocalName, path, values));
            }
        }
        if (root.Name.LocalName == "comprobanteRetencion")
        {
            // In v1 the support fields belong to impuesto; in v2 they belong to docSustento.
            foreach (var line in root.Descendants().Where(e => e.Name.LocalName is "impuesto" or "retencion" && e.Elements().Any(c => c.Name.LocalName == "valorRetenido")))
            {
                var support = line.Ancestors().FirstOrDefault(e => e.Name.LocalName == "docSustento");
                string? Support(string name) => ChildValue(line, name) ?? ChildValue(support, name);
                data.Retentions.Add(new(ChildValue(line, "codigo"), ChildValue(line, "codigoRetencion"),
                    DecimalValue(ChildValue(line, "baseImponible")), DecimalValue(ChildValue(line, "porcentajeRetener")),
                    DecimalValue(ChildValue(line, "valorRetenido")), Support("codDocSustento"), Support("numDocSustento"), Support("fechaEmisionDocSustento")));
            }
        }
        return data;
    }
}
