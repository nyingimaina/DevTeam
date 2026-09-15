import { StoppedProcessDto } from "../Chat/Data/BrokerTypes";

export function projectNameFromPath(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

export function formatCleanupNoticeMessage(
  workspacePath: string,
  stopped: StoppedProcessDto[],
): string | null {
  if (stopped.length === 0) return null;

  const projectName = projectNameFromPath(workspacePath);
  const names = stopped.map((s) => s.name).join(", ");
  const noun = stopped.length === 1 ? "process" : "processes";
  return `Stopped ${stopped.length} ${noun} from ${projectName}: ${names}`;
}
