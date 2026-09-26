interface PlanPanelProps {
  readonly plan: string | null | undefined;
}

/**
 * The agent's short plan, shown above the diff (FR-010).
 *
 * Above rather than beside or below, because it is what the diff is supposed to
 * be an implementation of. A reviewer who reads the intent first can notice a
 * diff that does something else; one who reads the diff first tends to check
 * only that it is internally coherent.
 */
export function PlanPanel({ plan }: PlanPanelProps) {
  if (!plan) {
    // Stated rather than rendered as an empty box. A run reaches a proposal only
    // through a plan, so a missing one is worth seeing rather than hiding.
    return (
      <section className="plan-panel plan-panel--empty" aria-label="Plan">
        <h2>Plan</h2>
        <p className="plan-panel__missing">No plan was recorded for this run.</p>
      </section>
    );
  }

  return (
    <section className="plan-panel" aria-label="Plan">
      <h2>Plan</h2>
      {plan
        .split('\n')
        .filter((paragraph) => paragraph.trim().length > 0)
        .map((paragraph, index) => (
          <p key={index} className="plan-panel__text">
            {paragraph}
          </p>
        ))}
    </section>
  );
}
