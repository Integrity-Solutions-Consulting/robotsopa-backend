using System.Net.Http.Json;
using SriAutomation.Application;
using SriAutomation.Domain;
namespace SriAutomation.LocalAgent;

// Authenticated loopback transport. Does not read passwords or control the desktop mouse/keyboard.
internal sealed class RemoteExecution(HttpClient client,string id):IStateRepository,IDownloadPipeline
{
    private string Path=>"/api/internal/agent/executions/"+Validation.Identifier(id);
    public T Read<T>(Func<State,T> read)=>read(client.GetFromJsonAsync<State>(Path).GetAwaiter().GetResult()??throw new IOException("No se pudo leer la operación."));
    public T Update<T>(Func<State,T> mutate){var state=client.GetFromJsonAsync<State>(Path).GetAwaiter().GetResult()??throw new IOException("No se pudo leer la operación.");var result=mutate(state);using var response=client.PutAsJsonAsync(Path,state).GetAwaiter().GetResult();response.EnsureSuccessStatusCode();return result;}
    public bool IsAvailable(Voucher document,string extension)=>client.GetFromJsonAsync<bool>(Path+"/available/"+Validation.Identifier(document.Id)+"/"+extension).GetAwaiter().GetResult();
    public Voucher Process(DownloadedVoucher file)
    {
        try
        {
            using var response=client.PostAsJsonAsync(Path+"/files",file).GetAwaiter().GetResult();
            if(!response.IsSuccessStatusCode) throw new DocumentTransferException("DocumentProcessingFailed");
            return response.Content.ReadFromJsonAsync<Voucher>().GetAwaiter().GetResult()??throw new DocumentTransferException("DocumentProcessingFailed");
        }
        catch(DocumentTransferException){throw;}
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or IOException){throw new DocumentTransferException("DocumentTransferFailed",ex);}
    }
}
internal sealed class DocumentTransferException(string code,Exception? inner=null):Exception(code,inner)
{
    public string Code { get; }=code;
}
