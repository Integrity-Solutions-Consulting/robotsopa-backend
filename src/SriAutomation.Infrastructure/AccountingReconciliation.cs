using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

// Only independently labelled PDF values are candidates. Identity checks happen before this method.
internal static class AccountingReconciliation
{
    public static void Merge(Voucher target, bool hasXml, Voucher? stored, Voucher? pdf)
    {
        var primary = hasXml ? target : stored;
        var fallback = hasXml ? stored : null;
        var observations = new List<string>();
        target.FieldEvidence = new Dictionary<string, FieldEvidence>();
        string? StringField(string name, Func<Voucher, string?> get)
        {
            var first = primary is null ? null : get(primary);
            var second = fallback is null ? null : get(fallback);
            var third = pdf is null ? null : get(pdf);
            if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(third) &&
                !string.Equals(first.Trim(), third.Trim(), StringComparison.OrdinalIgnoreCase)) observations.Add(name);
            if (!string.IsNullOrWhiteSpace(first)) { Evidence(name, hasXml ? "XML" : "Backend", primary!); return first; }
            if (!string.IsNullOrWhiteSpace(second)) { Evidence(name, "Backend", fallback!); return second; }
            if (!string.IsNullOrWhiteSpace(third)) { target.FieldEvidence[name] = new("PDF", "RotuloUnico"); return third; }
            return null;
        }
        decimal? Amount(string name, Func<Voucher, decimal?> get)
        {
            var first = primary is null ? null : get(primary);
            var second = fallback is null ? null : get(fallback);
            var third = pdf is null ? null : get(pdf);
            if (first.HasValue && third.HasValue && first.Value != third.Value) observations.Add(name);
            if (first.HasValue) { Evidence(name, hasXml ? "XML" : "Backend", primary!); return first; }
            if (second.HasValue) { Evidence(name, "Backend", fallback!); return second; }
            if (third.HasValue) { target.FieldEvidence[name] = new("PDF", "RotuloUnico"); return third; }
            return null;
        }
        void Evidence(string name, string source, Voucher value)
        {
            target.FieldEvidence[name] = source == "Backend" && value.FieldEvidence.TryGetValue(name, out var old)
                ? old : new(source, source == "XML" ? "IdentidadComprobada" : "Registrado");
        }
        // Read all candidates before assigning: target is also the primary for XML.
        var number = StringField("Numero", v => v.Number);
        var issuerTaxId = StringField("RucEmisor", v => v.IssuerTaxId);
        var issuer = StringField("Emisor", v => v.IssuerName);
        var recipient = StringField("Receptor", v => v.Recipient);
        var identification = StringField("Identificacion", v => v.Identification);
        var authorization = StringField("Autorizacion", v => v.Authorization);
        var authorizedAt = StringField("FechaAutorizacion", v => v.AuthorizedAt);
        var subtotal = Amount("Subtotal", v => v.Subtotal);
        var discount = Amount("Descuento", v => v.Discount);
        var taxes = Amount("Impuestos", v => v.Taxes);
        var total = Amount("Total", v => v.Total);
        target.Number = number; target.IssuerTaxId = issuerTaxId; target.IssuerName = issuer; target.Recipient = recipient;
        target.Identification = identification; target.Authorization = authorization; target.AuthorizedAt = authorizedAt;
        target.Subtotal = subtotal; target.Discount = discount; target.Taxes = taxes; target.Total = total;
        target.FieldEvidence["ClaveAcceso"] = new(hasXml ? "XML" : "Consulta", "IdentidadComprobada");
        target.FieldEvidence["FechaEmision"] = new(hasXml ? "XML" : "Consulta", "IdentidadComprobada");
        if (observations.Count > 0)
        {
            target.Status = "Revisar";
            target.Warning = string.Join(" ", new[] { target.Warning, "Hay datos registrados y del PDF que requieren conciliación: " + string.Join(", ", observations) + "." }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        else if (stored?.Warning?.Contains("requieren conciliación", StringComparison.Ordinal) == true)
        {
            target.Status = "Revisar";
            target.Warning = string.Join(" ", new[] { target.Warning, stored.Warning }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        }
    }
}
