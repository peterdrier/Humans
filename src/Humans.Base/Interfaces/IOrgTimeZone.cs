using NodaTime;

namespace Humans.Base.Interfaces;

/// <summary>
/// The organisation's time zone: the one admin setting every event and every org-local date (money, votes, surveys)
/// is read in. Served from memory, so views and sync code can use it. Settings implements it.
/// </summary>
public interface IOrgTimeZone
{
    DateTimeZone Zone { get; }
}
