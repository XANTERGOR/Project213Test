using System;
using LocalTerrainPrototype;
using Wait=LocalTerrainPrototype.LTRoadBatcherWait;

partial class Checks
{
    static void RoadBatcherWaitChecks()
    {
        var pending=new Wait.Sample(0,1,"Not initialized ()","pending shader");
        var compatible=new Wait.Sample(0,0,"","compatible shader");
        var bad=new Wait.Sample(0,2,"UnityPerMaterial property missing","missing property");
        Require(new Wait.Sample(-1,0,"","no subshader").status==Wait.Status.Pending,"Invalid subshader must not pass even with code zero");
        Require(new Wait.Sample(0,1," Not initialized ","").status==Wait.Status.Pending,"Known transient native status");
        Require(new Wait.Sample(0,1,"Not initialized; unrelated failure","").status==Wait.Status.Incompatible,"Do not swallow arbitrary reasons");
        Require(new Wait.Sample(0,9,null,"").status==Wait.Status.Incompatible,"Unknown native error is not retryable");
        Require(new Wait.Sample(0,9,"compatible","").status==Wait.Status.Incompatible,"Only code zero proves compatibility");

        var wait=new Wait(2,100);int first=0,second=0;
        Wait.Sample Inspect(int i)
        {
            if(i==0){first++;return compatible;}
            return ++second<3?pending:compatible;
        }
        Require(wait.Tick(100,Inspect)==Wait.Status.Pending&&first==0,"Warm-up update yields without native query");
        Require(wait.Tick(100,Inspect)==Wait.Status.Pending&&first==1&&second==1,"One ready shader is not enough");
        Require(wait.Tick(100.1,Inspect)==Wait.Status.Pending&&second==1,"Throttle native polling");
        Require(wait.Tick(100.25,Inspect)==Wait.Status.Pending&&first==1&&second==2,"Do not recheck an already verified shader");
        Require(wait.Tick(100.5,Inspect)==Wait.Status.Compatible&&second==3,"Deferred initialization eventually passes both shaders");
        Require(wait.Attempts==3&&wait.Summary.Contains("compatible shader"),"Diagnostic summary retained");
        Require(wait.Tick(200,_=>throw new Exception("terminal wait inspected again"))==Wait.Status.Compatible,"Terminal state is stable");

        wait=new Wait(2,0);wait.Tick(0,_=>pending);
        Require(wait.Tick(0,i=>i==0?pending:bad)==Wait.Status.Incompatible,"Real incompatibility fails without waiting for another shader");
        Require(wait.Summary.Contains("missing property"),"Native incompatibility reason preserved");

        wait=new Wait(1,0);wait.Tick(0,_=>pending);wait.Tick(0,_=>pending);
        Require(wait.Tick(Wait.TimeoutSeconds-.01,_=>pending)==Wait.Status.Pending,"Do not time out early");
        Require(wait.Tick(Wait.TimeoutSeconds,_=>pending)==Wait.Status.TimedOut,"Deadline overrides throttle, persistent pending is not PASS");
        Require(wait.Tick(99,_=>compatible)==Wait.Status.TimedOut,"No late silent conversion of timeout to PASS");

        wait=new Wait(1,0);wait.Tick(0,_=>pending);
        Require(wait.Tick(Wait.TimeoutSeconds,_=>compatible)==Wait.Status.Compatible,"Ready observation at deadline can complete");
        wait=new Wait(1,0);wait.Tick(0,_=>pending);
        Require(wait.Tick(Wait.TimeoutSeconds,_=>bad)==Wait.Status.Incompatible,"Real error at deadline takes precedence over timeout");

        wait=new Wait(1,0);wait.Tick(0,_=>pending);bool propagated=false;
        try{wait.Tick(0,_=>throw new InvalidOperationException("native API unavailable"));}
        catch(InvalidOperationException){propagated=true;}
        Require(propagated,"Native API exceptions must reach cleanup/failure handler");
        bool emptyRejected=false;try{new Wait(0,0);}catch(ArgumentOutOfRangeException){emptyRejected=true;}
        Require(emptyRejected,"An empty shader list cannot pass");
        Console.WriteLine("PASS road batcher wait: warm-up updates, throttling, independent shaders, recognized transient status only, native failures, deadline, exception propagation. Pure scheduling tests; not native Unity compatibility.");
    }
}
