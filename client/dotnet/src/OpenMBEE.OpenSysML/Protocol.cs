namespace OpenMBEE.OpenSysML;

/// <summary>The protocol a connection speaks to the service. A connection never switches or falls back.</summary>
public enum Protocol
{
    /// <summary>gRPC over HTTP/2, with protobuf messages: the default.</summary>
    Grpc = 0,
}
