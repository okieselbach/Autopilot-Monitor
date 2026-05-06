import { describe, it, expect } from "vitest";
import { computeWhiteGloveSplitSequence } from "../../app/sessions/[sessionId]/utils/eventHelpers";
import type { EnrollmentEvent } from "@/types";

// Fixture builder — keeps the tests focused on sequence/eventType only.
function ev(seq: number, eventType: string, phase = 0): EnrollmentEvent {
  return {
    eventId: `e${seq}`,
    sessionId: "s1",
    timestamp: new Date(2026, 4, 4, 10, 0, seq).toISOString(),
    eventType,
    severity: "Info",
    source: "Agent",
    phase,
    message: eventType,
    sequence: seq,
  };
}

describe("computeWhiteGloveSplitSequence", () => {
  it("returns -1 for a session with no WhiteGlove markers (single Device-Enrollment block)", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "esp_phase_changed"),
      ev(3, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(-1);
  });

  it("splits at the Part-2 agent_started that sits between whiteglove_complete and whiteglove_resumed", () => {
    // PR-A: orchestrator archives state and emits whiteglove_resumed ~0-2s after the
    // Part-2 agent_started boot. The user-perceived boundary is the boot itself, so the
    // post-reseal agent_started + its agent_version_check belong to the User Enrollment block.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(3, "agent_shutdown"),
      ev(4, "agent_started"),       // Part 2 boot — the actual split point
      ev(5, "whiteglove_resumed"),  // resumed marker arrives moments later
      ev(6, "esp_phase_changed"),
      ev(7, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(3); // 4 - 1
  });

  it("falls back to whiteglove_resumed.sequence-1 when no agent_started is found between Part-1 close and resume", () => {
    // Defensive: if the Part-2 boot event somehow isn't in the list (replay/filter), keep
    // the resumed marker as the boundary so Part-2 events still group correctly.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(3, "agent_shutdown"),
      ev(4, "whiteglove_resumed"),
      ev(5, "esp_phase_changed"),
      ev(6, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(3); // 4 - 1
  });

  it("falls back to first agent_started after whiteglove_complete when no whiteglove_resumed (older agents)", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(3, "agent_shutdown"),
      ev(4, "agent_started"),       // Part 2 boot — split point
      ev(5, "esp_phase_changed"),
      ev(6, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(3); // 4 - 1
  });

  it("returns the agent_shutdown sequence after whiteglove_complete when only Part 1 has finished (Pending)", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(3, "agent_shutdown"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(3);
  });

  it("returns whiteglove_complete.sequence when nothing follows it (Part 1 still wrapping up)", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(2);
  });

  it("splits at agent_started even when whiteglove_complete arrives late (race condition)", () => {
    // Race: Windows writes the WhiteGlove success event after the Part-2 reboot, so the
    // event list contains both signals. The Part-2 boot still is the boundary the user
    // perceives — whiteglove_complete then naturally lands inside the User Enrollment block.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "agent_shutdown"),
      ev(3, "agent_started"),       // Part 2 boot — split point
      ev(4, "whiteglove_resumed"),
      ev(5, "whiteglove_complete"), // arrived late from Windows
      ev(6, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(2); // 3 - 1
  });
});
