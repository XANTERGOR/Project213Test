using System;

namespace LocalTerrainPrototype
{
    // Pure scheduling policy: native Unity inspection stays in the validator.
    // A known uninitialized reply is retryable, never proof of compatibility.
    internal sealed class LTRoadBatcherWait
    {
        internal enum Status { Pending, Compatible, Incompatible, TimedOut }
        internal readonly struct Sample
        {
            internal readonly Status status;
            internal readonly string diagnostic;
            internal Sample(int subshader,int code,string reason,string diagnostic)
            {
                this.diagnostic=diagnostic;
                string text=(reason??"").Trim();
                status=subshader<0?Status.Pending:code==0?Status.Compatible:
                    text=="Not initialized"||text=="Not initialized ()"?Status.Pending:Status.Incompatible;
            }
        }
        internal const double TimeoutSeconds=15;
        const double PollSeconds=.25;
        readonly bool[] ready;
        readonly string[] diagnostics;
        readonly double deadline;
        double nextPoll;
        int updates;
        internal Status Result { get; private set; }
        internal int Attempts { get; private set; }
        internal string Summary=>string.Join("; ",diagnostics);

        internal LTRoadBatcherWait(int count,double now)
        {
            if(count<=0)throw new ArgumentOutOfRangeException(nameof(count));
            ready=new bool[count];diagnostics=new string[count];
            for(int i=0;i<count;i++)diagnostics[i]="Shader "+i+": not queried";
            deadline=now+TimeoutSeconds;nextPoll=now;
        }
        internal Status Tick(double now,Func<int,Sample> inspect)
        {
            if(Result!=Status.Pending)return Result;
            // Let Unity consume pass initialization first; never block its editor loop.
            if(++updates<2||(now<nextPoll&&now<deadline))return Result;
            nextPoll=now+PollSeconds;Attempts++;
            bool allReady=true;
            for(int i=0;i<ready.Length;i++)
            {
                if(ready[i])continue;
                var sample=inspect(i);diagnostics[i]=sample.diagnostic;
                if(sample.status==Status.Incompatible)return Result=Status.Incompatible;
                ready[i]=sample.status==Status.Compatible;allReady&=ready[i];
            }
            return Result=allReady?Status.Compatible:now>=deadline?Status.TimedOut:Status.Pending;
        }
    }
}
