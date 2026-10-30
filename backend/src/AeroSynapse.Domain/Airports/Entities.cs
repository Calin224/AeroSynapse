namespace AeroSynapse.Domain.Airports;

public interface IMapElement
{
    Guid Id { get; }
    string Name { get; }
    bool IsActive { get; }
}

public interface IAirportElement : IMapElement
{
    Guid AirportId { get; }
}

public interface ITerminalElement : IAirportElement
{
    Guid TerminalId { get; }
}

public enum ZoneType
{
    Unknown, Entrance, Walkway, CheckIn, Security, PassportControl,
    Gate, Lounge, BaggageClaim, Stairs, Elevator
}

public enum ResourceType
{
    Unknown, Staff, SecurityLane, CheckInDesk, Equipment
}

public sealed class Airport : IMapElement
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class Terminal : IAirportElement
{
    public Guid Id { get; set; }
    public Guid AirportId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class Floor : ITerminalElement
{
    public Guid Id { get; set; }
    public Guid AirportId { get; set; }
    public Guid TerminalId { get; set; }
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public double ElevationMeters { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Zone : ITerminalElement
{
    public Guid Id { get; set; }
    public Guid AirportId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid FloorId { get; set; }
    public string Name { get; set; } = "";
    public ZoneType Type { get; set; }
    public int Capacity { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Route : ITerminalElement
{
    public Guid Id { get; set; }
    public Guid AirportId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid FromZoneId { get; set; }
    public Guid ToZoneId { get; set; }
    public string Name { get; set; } = "";
    public double TravelDurationSeconds { get; set; }
    public bool IsBidirectional { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public sealed class Checkpoint : ITerminalElement
{
    public Guid Id { get; set; }
    public Guid AirportId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid ZoneId { get; set; }
    public Guid? ResourcePoolId { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
    public double ProcessingRatePerMinute { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Gate : ITerminalElement
{
    public Guid Id { get; set; }
    public Guid AirportId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid ZoneId { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
    public double BoardingRatePerMinute { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ResourcePool : ITerminalElement
{
    public Guid Id { get; set; }
    public Guid AirportId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid ZoneId { get; set; }
    public string Name { get; set; } = "";
    public ResourceType Type { get; set; }
    public int Capacity { get; set; }
    public bool IsActive { get; set; } = true;
}
