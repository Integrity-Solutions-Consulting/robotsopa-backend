using SriAutomation.Application;
using SriAutomation.Domain;
namespace SriAutomation.Tests;
public sealed class AuthenticationFlowTests
{
 [Fact] public void WorkerAuthenticationIsActiveOnlyWhileProcessing() {
  var op = new AuthenticationOperation { Owner="Worker", Status="OpeningBrowser" };
  Assert.True(AuthenticationFlow.Active(op)); op.Status="RequiresHumanAction"; Assert.False(AuthenticationFlow.Active(op));
  op.Owner="Agent"; op.Status="OpeningBrowser"; Assert.False(AuthenticationFlow.Active(op));
 }
 [Fact] public void QueueDoesNotDependOnAgentHeartbeat() {
  var s = new State { Accounts=[new("a","Cuenta","1790000000001")] };
  var e=Validation.Enqueue(s,new("a",new(2025,1,1),new(2025,1,1),["Facturas"]));
  Assert.Equal("Pending",e.Status); Assert.Single(s.Executions);
 }
}
