namespace Maldex.Core.Domain;

public enum RunStatus { Queued, Preparing, Running, Collecting, Processing, Completed, Failed, Cancelled }
public enum NetworkMode { None, Simulated, LiveVpn, LiveTor }
public enum Verdict { Unknown, NoThreats, Suspicious, Malicious }
public enum UserRole { Analyst, Admin }
public enum EventType { Process, File, Registry, Network, Dns }
public enum DetectionSource { Suricata, Yara, Heuristic }
public enum ArtifactKind { Sample, Dropped, Pcap, Screenshot, Report, MitmFlows }
public enum TemplateVersionOrigin { IsoBuild, Edit, Refresh, Imported }
public enum TemplateVersionStatus { Ready, Retired, Deleted }
