using LocalTerrainPrototype;

static class MaterialRevisionChecks
{
    sealed class EqualButDistinct
    {
        public override bool Equals(object other)=>other is EqualButDistinct;
        public override int GetHashCode()=>0;
    }
    public static void Run(Action<bool,string> check)
    {
        using var registry=new LTDetailMaterialRevisionProbe<object>();
        object a=new EqualButDistinct(),b=new EqualButDistinct();
        var absent=registry.Capture(a);
        check(!absent.CanReuse(absent),"unowned never reuses, including default/default");
        check(!registry.Capture(null).CanReuse(absent),"null is unowned");
        using var owner=registry.ClaimExclusive(a);
        var ticket=registry.Capture(a);
        for(int i=0;i<256;i++)check(registry.Capture(a).CanReuse(ticket),"unchanged explicitly owned value stable");
        check(!registry.Capture(b).CanReuse(ticket),"reference identity, not overloaded Equals");
        bool duplicate=false;try{registry.ClaimExclusive(a);}catch(InvalidOperationException){duplicate=true;}
        check(duplicate,"duplicate exclusive owner rejected");
        owner.NotifyChanged();check(!registry.Capture(a).CanReuse(ticket),"notification invalidates previous consumer receipt");
        ticket=registry.Capture(a);
        owner.Mutate(()=>
        {
            var pending=registry.Capture(a);
            check(!pending.CanReuse(ticket)&&!pending.CanReuse(pending),"in-progress write cannot publish or reuse a receipt");
            owner.Mutate(()=>check(!registry.Capture(a).CanReuse(ticket),"nested write stays uncacheable"));
            check(!registry.Capture(a).CanReuse(ticket),"outer write still in progress");
        });
        check(!registry.Capture(a).CanReuse(ticket),"completed write invalidates old receipt");
        ticket=registry.Capture(a);
        try{owner.Mutate(()=>throw new ApplicationException("injected partial write"));}catch(ApplicationException){}
        check(!registry.Capture(a).CanReuse(ticket),"throwing partial write still invalidates");
        var firstConsumer=registry.Capture(a);var secondConsumer=firstConsumer;
        owner.NotifyChanged();firstConsumer=registry.Capture(a);
        check(firstConsumer.CanReuse(registry.Capture(a))&&!secondConsumer.CanReuse(firstConsumer),"consumers track receipts independently");
        owner.Dispose();check(!registry.Capture(a).CanReuse(firstConsumer)&&!firstConsumer.CanReuse(firstConsumer),"revocation invalidates captured tickets too");
        using var replacement=registry.ClaimExclusive(a);
        check(!registry.Capture(a).CanReuse(firstConsumer),"re-registration has new identity even at same revision");
        owner.Dispose();check(registry.Capture(a).CanReuse(registry.Capture(a)),"old owner double-dispose cannot remove replacement");
        using var second=registry.ClaimExclusive(b);ticket=registry.Capture(a);
        var secondTicket=registry.Capture(b);registry.RevokeAll();
        check(!registry.Capture(a).CanReuse(ticket)&&!registry.Capture(b).CanReuse(secondTicket),"uncertain ownership revokes all, no auto reacquire");
        bool stale=false;try{replacement.NotifyChanged();}catch(ObjectDisposedException){stale=true;}
        check(stale,"stale owner cannot silently resume tracking");
        using var overflowOwner=registry.ClaimExclusive(a);
        // Exhaustion must fail closed rather than collide with an ancient ticket.
        var field=overflowOwner.GetType().GetField("state",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
        var state=(LTDetailMaterialRevisionProbe<object>.State)field.GetValue(overflowOwner);
        state.revision=ulong.MaxValue;ticket=registry.Capture(a);
        bool overflow=false;try{overflowOwner.NotifyChanged();}catch(InvalidOperationException){overflow=true;}
        check(overflow&&!ticket.CanReuse(ticket),"revision exhaustion revokes instead of wrapping");
        using var beforeDispose=registry.ClaimExclusive(b);ticket=registry.Capture(b);registry.Dispose();
        check(!registry.Capture(b).CanReuse(ticket)&&!ticket.CanReuse(ticket),"disposed registry invalidates all receipts");
        bool disposed=false;try{registry.ClaimExclusive(a);}catch(ObjectDisposedException){disposed=true;}
        check(disposed,"disposed registry cannot accept new ownership");
    }
}
