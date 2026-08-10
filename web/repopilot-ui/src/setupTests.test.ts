import { describe, expect, it } from 'vitest';

// Smoke test proving the test harness itself is wired: jsdom environment,
// globals, and the jest-dom matchers loaded from setupTests.ts. Component tests
// arrive with the review UI in User Story 1.
describe('test harness', () => {
  it('runs in a DOM environment', () => {
    const el = document.createElement('div');
    el.textContent = 'ready';
    document.body.appendChild(el);

    expect(el).toBeInTheDocument();
    expect(el).toHaveTextContent('ready');
  });
});
