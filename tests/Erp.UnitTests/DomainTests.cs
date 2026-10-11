using Erp.BuildingBlocks.Domain;
using Erp.Inventory.Domain;
using Erp.Localization.Application;
using Erp.Procurement.Domain;
using Erp.Workflow.Domain;
using Erp.Audit.Domain;
using Erp.Analytics.Application;
namespace Erp.UnitTests;

public class StockLevelTests
{
    [Fact]
    public void Receive_updates_weighted_average_cost()
    {
        var l = new StockLevel(Guid.NewGuid(), Guid.NewGuid());
        l.Receive(10, 5m); l.Receive(10, 7m);
        Assert.Equal(20, l.OnHand); Assert.Equal(6m, l.AverageCost);
    }
    [Fact]
    public void Issue_beyond_available_is_rejected_and_reservations_reduce_availability()
    {
        var l = new StockLevel(Guid.NewGuid(), Guid.NewGuid());
        l.Receive(10, 1m); l.Reserve(4);
        Assert.Equal(6, l.Available);
        Assert.Throws<DomainException>(() => l.Issue(7));
        l.Issue(6); Assert.Equal(4, l.OnHand);
    }
}

public class PurchaseOrderTests
{
    private static PurchaseOrder NewPo()
    {
        var po = new PurchaseOrder("PO-1", Guid.NewGuid(), Guid.NewGuid(), "USD", null, "alice");
        po.AddLine(Guid.NewGuid(), "SKU1", "Widget", 10, 5m, 0.10m);
        return po;
    }
    [Fact] public void Totals_include_tax() { var po = NewPo(); Assert.Equal(50m, po.Subtotal); Assert.Equal(5m, po.TaxTotal); Assert.Equal(55m, po.Total); }
    [Fact] public void Cannot_receive_before_approval() => Assert.Throws<DomainException>(() => NewPo().Receive([(Guid.NewGuid(), 1)]));
    [Fact]
    public void Partial_then_full_receipt_moves_status_and_blocks_over_receipt()
    {
        var po = NewPo(); po.Submit(); po.Approve("bob", null);
        var lineId = po.Lines.First().Id;
        po.Receive([(lineId, 4)]); Assert.Equal(PurchaseOrderStatus.PartiallyReceived, po.Status);
        Assert.Throws<DomainException>(() => po.Receive([(lineId, 7)]));
        po.Receive([(lineId, 6)]); Assert.Equal(PurchaseOrderStatus.Received, po.Status);
    }
}

public class WorkflowRuleTests
{
    [Theory]
    [InlineData("[{\"field\":\"totalAmount\",\"operator\":\"gt\",\"value\":10000}]", "{\"totalAmount\":12000}", true)]
    [InlineData("[{\"field\":\"totalAmount\",\"operator\":\"gt\",\"value\":10000}]", "{\"totalAmount\":900}", false)]
    [InlineData("[{\"field\":\"currency\",\"operator\":\"in\",\"value\":[\"USD\",\"EUR\"]}]", "{\"currency\":\"eur\"}", true)]
    [InlineData(null, "{}", true)]
    public void Rules_evaluate(string? rules, string payload, bool expected) => Assert.Equal(expected, RuleEvaluator.Evaluate(rules, payload));

    [Fact]
    public void Steps_not_matching_rules_are_skipped_and_requester_cannot_approve()
    {
        var def = new WorkflowDefinition("po", "PO approval", "PurchaseOrder", 1,
        [
            new WorkflowStepDefinition(1, "Manager", "Manager", null, null),
            new WorkflowStepDefinition(2, "Director", "Director", null, "[{\"field\":\"totalAmount\",\"operator\":\"gt\",\"value\":10000}]")
        ]);
        var small = WorkflowInstance.Start(def, "PurchaseOrder", Guid.NewGuid(), "{\"totalAmount\":500}", "alice");
        Assert.Single(small.Tasks);
        Assert.Throws<ForbiddenException>(() => small.Decide(Guid.NewGuid(), "alice", ["Manager"], true, null));
        small.Decide(Guid.NewGuid(), "bob", ["Manager"], true, null);
        Assert.Equal(WorkflowStatus.Approved, small.Status);
        var big = WorkflowInstance.Start(def, "PurchaseOrder", Guid.NewGuid(), "{\"totalAmount\":50000}", "alice");
        Assert.Equal(2, big.Tasks.Count);
    }
}

public class TaxTests
{
    [Fact]
    public void Stacked_tax_exclusive()
    {
        var r = TaxCalculator.Calculate(100m, false, [new("CGST", 0.09m, 1, false), new("SGST", 0.09m, 2, false)]);
        Assert.Equal(18m, r.TaxTotal); Assert.Equal(118m, r.Gross);
    }
    [Fact]
    public void Inclusive_price_is_decomposed_back_to_net()
    {
        var r = TaxCalculator.Calculate(118m, true, [new("GST", 0.18m, 1, false)]);
        Assert.Equal(100m, r.Net); Assert.Equal(18m, r.TaxTotal);
    }
    [Fact]
    public void Compound_tax_applies_on_running_total()
    {
        var r = TaxCalculator.Calculate(100m, false, [new("Federal", 0.05m, 1, false), new("Provincial", 0.10m, 2, true)]);
        Assert.Equal(5m, r.Lines[0].Amount); Assert.Equal(10.5m, r.Lines[1].Amount);
    }
}

public class AuditChainTests
{
    [Fact]
    public void Hash_changes_when_any_field_is_tampered()
    {
        var t = Guid.NewGuid(); var now = DateTime.UtcNow;
        var a = new AuditLogEntry(Guid.NewGuid(), t, "inventory", "Product", "1", "Added", "{}", null, "u", null, now, AuditLogEntry.Genesis);
        var b = new AuditLogEntry(Guid.NewGuid(), t, "inventory", "Product", "1", "Modified", "{}", null, "u", null, now, a.Hash);
        Assert.Equal(a.Hash, a.ComputeHash());
        Assert.Equal(a.Hash, b.PreviousHash);
        Assert.NotEqual(a.Hash, b.Hash);
    }
}

public class ForecastTests
{
    [Fact]
    public void Smoothing_on_constant_series_has_zero_error()
    {
        var (level, mae) = Smoothing.Fit(Enumerable.Repeat(5m, 20).ToList(), 0.3);
        Assert.Equal(5m, level); Assert.Equal(0m, mae);
    }
}
