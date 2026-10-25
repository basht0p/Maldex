namespace Maldex.Core.Domain;

public enum RunStatus { Queued, Preparing, Running, Collecting, Processing, Completed, Failed, Cancelled }
public enum NetworkMode { None, Simulated, LiveVpn, LiveTor, Direct }
public enum Verdict { Unknown, NoThreats, Suspicious, Malicious }
public enum UserRole { Analyst, Admin }
public enum EventType { Process, File, Registry, Network, Dns }
public enum DetectionSource { Suricata, Yara, Heuristic }
public enum ArtifactKind { Sample, Dropped, Pcap, Screenshot, Report, MitmFlows }
public enum TemplateVersionOrigin { IsoBuild, Edit, Refresh, Imported }
public enum TemplateVersionStatus { Ready, Retired, Deleted }

public enum TemplateJobKind { Edit, Refresh, Build }
public enum TemplateJobState { Queued, Preparing, Configuring, Sealing, Verifying, Published, Sealed, Failed, Cancelled, Installing, Provisioning }

public enum MediaKind { OsInstaller, Application }
public enum MediaSource { Upload, Url }
public enum MediaStatus { Receiving, Ready, Failed, Deleted }
