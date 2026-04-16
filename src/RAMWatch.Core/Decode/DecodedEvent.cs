namespace RAMWatch.Core.Decode;

/// <summary>
/// Result of decoding a MonitoredEvent into something a RAM tuner can read.
/// Pure data — no display logic. The GUI renders the four prose sections
/// as labelled paragraphs and the facts list as a key-value grid.
/// </summary>
public sealed record DecodedEvent(
    string Title,
    string What,
    string Where,
    string Why,
    string WhatToDo,
    IReadOnlyList<KeyValuePair<string, string>> Facts
);
