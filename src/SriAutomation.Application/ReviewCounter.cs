using SriAutomation.Domain;

namespace SriAutomation.Application;

public static class ReviewCounter
{
    public static void Update(State state, Execution job)
    {
        job.Reviews = state.Documents.Where(d => d.Account == job.Query.Account && d.Issued >= job.Query.From &&
            d.Issued <= job.Query.To && job.Query.Types.Contains(d.Type) &&
            (job.Query.Mode == "Stored" || job.AttemptedKeys.Contains(d.AccessKey)) &&
            (!string.IsNullOrWhiteSpace(d.XmlPath) || !string.IsNullOrWhiteSpace(d.PdfPath)) &&
            (d.Status != "ValidadoXML" || !string.IsNullOrWhiteSpace(d.Warning)))
            .DistinctBy(d => (d.Type, d.AccessKey)).Count();
    }
}
