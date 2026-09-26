using System.Text.Json;
using RepoPilot.Application.Ports;

namespace RepoPilot.E2ETests;

/// <summary>
/// Stands in for the model provider so an end-to-end run is deterministic and
/// needs no credential.
/// <para>
/// It behaves the way a real turn behaves — a plan and a tool call arrive
/// together, and the proposal is full file content rather than a diff hunk. The
/// content it proposes genuinely fixes the fixture's defect, so the sandboxed
/// test run really does go from failing to passing. Nothing downstream of this
/// class knows it is not a model.
/// </para>
/// </summary>
public sealed class ScriptedChatAdapter : IChatProviderAdapter
{
    /// <summary>
    /// The fixed file. The defect is that <c>ShippingAddress</c> is dereferenced
    /// without a null check, which the second test exercises.
    /// </summary>
    public const string FixedOrderLookupService = """
        namespace Orders;

        /// <summary>
        /// Looks up orders and projects them for the API surface.
        /// </summary>
        public sealed class OrderLookupService(IOrderRepository repository)
        {
            private readonly IOrderRepository _repository = repository;

            /// <summary>
            /// Returns a view of the order, or <c>null</c> when no such order exists.
            /// </summary>
            public OrderView? Lookup(string orderId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

                var order = _repository.Find(orderId);
                if (order is null)
                {
                    return null;
                }

                return new OrderView(
                    order.Id,
                    order.CustomerName,
                    order.ShippingAddress?.City,
                    order.ShippingAddress?.PostalCode,
                    order.TotalAmount);
            }
        }

        """;

    public const string Plan =
        "The order lookup dereferences ShippingAddress without checking it. " +
        "Guard both projections so an order with no address on file returns a view " +
        "with null shipping fields instead of throwing.";

    public string ModelId => "scripted-e2e";

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var entries = new[]
        {
            new
            {
                path = "src/Orders/OrderLookupService.cs",
                operation = "modify",
                new_content = FixedOrderLookupService,
            },
        };

        var arguments = JsonSerializer.Serialize(new
        {
            summary = "Guard the shipping address projection against a null address.",
            entries,
        });

        // Text and tool call in one turn, with stop reason tool_use — the shape
        // a real response has. An agent that ended its turn after only a plan
        // would mean "finished without proposing", which is a different outcome.
        return Task.FromResult(new ChatCompletion(
            Plan,
            [new ToolCall("call-e2e-1", "propose_patch", arguments)],
            ChatStopReason.ToolUse,
            new ProviderUsage(1200, 400, 0, 0),
            ModelId));
    }
}
