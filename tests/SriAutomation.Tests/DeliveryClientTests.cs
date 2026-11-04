using System.Net;
using System.Net.Http.Json;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;
public class DeliveryClientTests
{
    [Fact]
    public async Task ReconnectAfterLostAcknowledgementDoesNotDuplicateFileOrRows()
    {
        var root=Path.Combine(Path.GetTempPath(),"sri-reconnect-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            byte[] bytes=[1,2,3]; var doc=new Voucher{Source="Sri",Type="Facturas",AccessKey=new string('1',49),Issued=new DateOnly(2026,4,1)};
            var handler=new Stub(new DeliveryPayload("item","1790000000001","pdf",DownloadPipeline.Hash(bytes),doc),bytes);
            using var http=new HttpClient(handler){BaseAddress=new Uri("https://example.test")};
            var client=new DeliveryClient(http,new WindowsDesktopStorage(root));
            await Assert.ThrowsAsync<HttpRequestException>(()=>client.Tick(CancellationToken.None));
            await new DeliveryClient(http,new WindowsDesktopStorage(root)).Tick(CancellationToken.None);
            Assert.Equal(2,handler.Acks);
            using var book=new ClosedXML.Excel.XLWorkbook(Path.Combine(root,"SRI-Automation","1790000000001","2026","04","Resumen_2026_04.xlsx"));
            Assert.Equal(2,book.Worksheet(1).LastRowUsed()!.RowNumber());
        } finally { Directory.Delete(root,true); }
    }
    private sealed class Stub(DeliveryPayload payload, byte[] content):HttpMessageHandler
    {
        public int Acks;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method==HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/next")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(payload)});
            if(request.Method==HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(content)});
            Acks++; return Task.FromResult(new HttpResponseMessage(Acks==1?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK));
        }
    }
}
