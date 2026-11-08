using System.Xml;
namespace SriAutomation.Api;

// Stable, allowlisted codes; never log exception messages or request content.
public static class FileRejectionDiagnostics
{
    public static string Code(string stage, Exception ex) => ex switch
    {
        XmlException => "XML_MALFORMED",
        ArgumentException when ex.Message == "La descarga PDF no contiene un PDF válido." => "PDF_INVALID",
        ArgumentException when ex.Message == "PDF sin páginas." => "PDF_NO_PAGES",
        ArgumentException when ex.Message == "El PDF pertenece a una clave diferente." => "PDF_KEY_MISMATCH",
        ArgumentException when ex.Message == "La fecha de emisión PDF no coincide con la fila." => "PDF_DATE_MISMATCH",
        ArgumentException when ex.Message == "El emisor del XML no corresponde a la cuenta seleccionada." => "ISSUER_ACCOUNT_MISMATCH",
        ArgumentException when ex.Message == "PDF y XML pertenecen a comprobantes diferentes." => "PDF_XML_KEY_MISMATCH",
        ArgumentException when ex.Message == "El documento no coincide con la clave, tipo o emisión de la fila consultada." => "DOCUMENT_ROW_MISMATCH",
        ArgumentException when ex.Message == "Colisión de integridad en el manifiesto." => "HISTORICAL_HASH_MISMATCH",
        ArgumentException when ex.Message == "Cuenta no registrada." => "ACCOUNT_NOT_REGISTERED",
        ArgumentException when ex.Message == "No hay archivos disponibles para este comprobante." => "NO_FILE",
        ArgumentException when ex.Message == "Tamaño de archivo inválido." => "FILE_SIZE_INVALID",
        ArgumentException when ex.Message == "Identificador inválido." => "IDENTIFIER_INVALID",
        ArgumentException when stage == "Authorization" => "JOB_SCOPE_REJECTED",
        ArgumentException when stage == "Ownership" => "OWNERSHIP_REQUEST_INVALID",
        ArgumentException when stage == "Parse" => "DOCUMENT_PARSE_REJECTED",
        InvalidDataException => "INTEGRITY_INVALID",
        ArgumentException => "FILE_VALIDATION_OTHER",
        _ => "NON_400_ERROR"
    };
}
