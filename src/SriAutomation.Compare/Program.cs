using SriAutomation.Infrastructure;

if (args.Length != 2)
{
    Console.Error.WriteLine("Uso: dotnet run --project src/SriAutomation.Compare -- <archivo.xml> <reporte.xlsx>");
    return 2;
}
try
{
    var result = XmlExcelComparator.CompareDetailed(File.ReadAllBytes(args[0]), args[1]);
    foreach (var item in result.Verified) Console.WriteLine($"Cobertura: {item}");
    foreach (var item in result.NotVerified) Console.WriteLine($"No verificado: {item}");
    foreach (var d in result.Differences) Console.WriteLine($"{d.Field}: {d.Kind}; esperado={d.Expected}; encontrado={d.Actual}");
    Console.WriteLine($"Diferencias: {result.Differences.Count}. La comparación es parcial.");
    return result.Differences.Count == 0 ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"No se pudo comparar: {ex.GetType().Name}.");
    return 2;
}
