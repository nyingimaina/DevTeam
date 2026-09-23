import { NegotiationPointDto, NegotiationPointStatus, NegotiationResponseKind } from "../../Chat/Data/BrokerTypes";

/**
 * Plain wording for the push-back exchange. The end user is non-technical: no stage jargon, no
 * "gate"/"push back"/"round-trip" talk — just "the team sent this back, here is what's open".
 */
export function negotiationHeading(points: NegotiationPointDto[]): string {
  if (points.length === 0) {
    return "";
  }

  const round = Math.max(...points.map((p) => p.round));
  const open = points.filter((p) => p.status === "Open").length;
  const plural = open === 1 ? "point" : "points";

  return open === 0
    ? `Back-and-forth with the team — round ${round}, everything addressed`
    : `Back-and-forth with the team — round ${round}, ${open} ${plural} still open`;
}

export function pointStatusLabel(status: NegotiationPointStatus): string {
  switch (status) {
    case "Resolved":
      return "Closed";
    case "Escalated":
      return "Needs your decision";
    default:
      return "Open";
  }
}

export function pointResponseLabel(kind: NegotiationResponseKind): string {
  switch (kind) {
    case "Addressed":
      return "Says this is done";
    case "Disputed":
      return "Disagrees with this point";
    case "Blocked":
      return "Can't do this as asked";
    default:
      return "No answer yet";
  }
}
