/** EUROCONTROL Standard Inputs v10.0.2: EU25 value of a passenger hour, personal travel, 2025 prices. */
const PASSENGER_HOUR = 22.89;
/** Eurostat: total hourly labour cost in Romania, 2024. */
const OPERATOR_HOUR = 12.5;

/** Shown on hover beside an estimate, so the rates stay traceable without a panel. */
export const COST_BASIS =
  'Timpul economisit de pasageri la 22,89 €/oră (EUROCONTROL), minus orele de operator adăugate la 12,5 €/oră (Eurostat, România 2024). Estimare din valori publice, nu date contabile.';

/** Passenger time saved, valued per hour, minus the operator hours added (or plus the ones released). */
export function netSaving(passengerHoursSaved: number, operatorHoursAdded: number) {
  return passengerHoursSaved * PASSENGER_HOUR - operatorHoursAdded * OPERATOR_HOUR;
}

/** The estimate as one short phrase: a saving, or a cost when the plan loses money. */
export function costText(net: number) {
  const amount = `${Math.abs(Math.round(net)).toLocaleString('ro-RO')} €`;
  return net >= 0 ? `Economie estimată: ${amount}` : `Cost estimat: ${amount}`;
}
