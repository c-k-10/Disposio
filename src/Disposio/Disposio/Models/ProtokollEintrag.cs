using System;

namespace Disposio.Models;

/// <summary>
/// Eine Zeile aus public.aktionsprotokoll für die Protokoll-Ansichten (K05 und Dashboard).
/// Firma ist leer bei Einträgen ohne Unternehmen, zum Beispiel aus der Entwicklungsansicht.
/// </summary>
public sealed record ProtokollEintrag(DateTime Zeitpunkt, string Wer, string? Firma, string Aktion, string Details);
