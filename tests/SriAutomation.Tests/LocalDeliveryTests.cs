using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.Automation;
namespace SriAutomation.Tests;
public class LocalDeliveryTests
{
 [Fact] public void HistoricalDeliveryRemainsPendingAfterRecovery() {
  var dir=Path.Combine(Path.GetTempPath(),"sri-delivery-"+Guid.NewGuid().ToString("N"));
  try { var r=new JsonStateRepository(dir); r.Update(s=>{s.LocalDeliveries.Add(new(){Id="delivery",ExecutionId="job",Status="Pending",Sha256="hash"});s.Executions.Add(new(){Id="job",Status="WaitingForDelivery",Query=new("a",new(2025,1,1),new(2025,1,1),["Facturas"],StorageMode:"LOCAL_WINDOWS")});return true;});
   var processor=new JobProcessor(r,new DocumentStorage(Path.Combine(dir,"downloads")),new SriPortal());processor.Recover();
   Assert.Equal("Pending",r.Read(s=>s.LocalDeliveries.Single().Status));Assert.Equal("WaitingForDelivery",r.Read(s=>s.Executions.Single().Status));
  } finally { if(Directory.Exists(dir))Directory.Delete(dir,true); }
 }
 [Fact] public void NewWindowsDestinationsAreRejected() {
  var s=new State{Accounts=[new("a","Cuenta")]}; var q=new Query("a",new(2025,1,1),new(2025,1,1),["Facturas"],StorageMode:"LOCAL_WINDOWS");
  Assert.Throws<ArgumentException>(()=>Validation.Enqueue(s,q)); Assert.Empty(s.Executions);
 }
}
