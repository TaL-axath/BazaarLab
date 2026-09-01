# Rejected TempoMeterController phase source

LocalCapture v0.5.0/v0.5.1 briefly experimented with reading
`TempoMeterController._timeSinceTempoGainMs` as the opening combat Tempo phase.
Static control-flow inspection disproves that interpretation:

- `Initialize` always assigns `_timeSinceTempoGainMs = 0`.
- `UpdateTempo` changes only the displayed Tempo value and does not synchronize
  or reset `_timeSinceTempoGainMs` when an actual Tempo gain arrives.
- `OnCombatSimFrameAdvanced` advances and wraps the field as an independent UI
  animation cycle.

The field is therefore a visual overlay phase, not authoritative server combat
state. Writing it as `TempoGainCooldownRemaining` would replace honest seeded
marginalization with a confidently wrong value. The installed bridge was
restored to v0.4.0.
