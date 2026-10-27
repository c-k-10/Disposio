using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Erstellt die Monatsstatistik als einseitiges A4-PDF und speichert es im Download-Ordner.
/// Ohne zusätzliche Bibliothek: Für eine Seite mit Text und Linien reicht der kleine PdfSeite-Schreiber unten.
/// </summary>
public static class StatistikPdf
{
    private static readonly CultureInfo Deutsch = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Speichert das PDF unter „Downloads“ und gibt den vollständigen Dateipfad zurück.</summary>
    public static string InDownloadsSpeichern(MonatsStatistik statistik, DateTime erstelltAm)
    {
        string ordner = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(ordner);

        // Firmennamen können Zeichen enthalten, die in Dateinamen verboten sind (zum Beispiel „/“).
        string firma = new string(statistik.Firmenname.Select(zeichen =>
            Path.GetInvalidFileNameChars().Contains(zeichen) ? '_' : zeichen).ToArray()).Trim();
        string name = $"Disposio Statistik {StatistikTexte.Lang(statistik.Monat)} - {firma}";

        // Eine vorhandene Datei wird nicht überschrieben: „… (2).pdf“, „… (3).pdf“ usw.
        string pfad = Path.Combine(ordner, name + ".pdf");
        for (int nummer = 2; File.Exists(pfad); nummer++)
            pfad = Path.Combine(ordner, $"{name} ({nummer}).pdf");

        // CreateNew: Falls zwischen Prüfung und Schreiben doch eine Datei entsteht, gibt es einen Fehler statt Überschreiben.
        using var datei = new FileStream(pfad, FileMode.CreateNew, FileAccess.Write);
        datei.Write(Erstellen(statistik, erstelltAm));
        return pfad;
    }

    /// <summary>Das fertige PDF als Bytes.</summary>
    public static byte[] Erstellen(MonatsStatistik statistik, DateTime erstelltAm)
    {
        const double links = 56, rechts = 539, werteSpalte = 340;
        var seite = new PdfSeite();

        seite.Text(links, 784, "Monatsstatistik " + StatistikTexte.Lang(statistik.Monat), 20, fett: true);
        seite.Text(links, 760, statistik.Firmenname, 13);
        seite.Text(links, 742, $"Erstellt am {erstelltAm.ToString("dd.MM.yyyy 'um' HH:mm 'Uhr'", Deutsch)}", 9, grau: true);
        seite.Linie(links, 728, rechts, 728, dicke: 1);

        seite.Text(links, 708, "Kennzahl", 10, fett: true, grau: true);
        seite.Text(werteSpalte, 708, "Wert", 10, fett: true, grau: true);

        double y = 684;
        foreach (StatistikZeile zeile in StatistikTexte.Zeilen(statistik))
        {
            seite.Text(links, y, zeile.Bezeichnung, 11, fett: zeile.Hervorgehoben);
            seite.Text(werteSpalte, y, zeile.Wert, 11, fett: zeile.Hervorgehoben);
            seite.Linie(links, y - 8, rechts, y - 8, dicke: 0.4);
            y -= 26;
        }

        seite.Text(links, 60, "Werte für den Kalendermonat in deutscher Zeit.", 8, grau: true);
        seite.Text(links, 48, "Tankvorgänge, Wartungen und Schäden zählen über die Fahrzeuge des Unternehmens.", 8, grau: true);
        seite.Text(links, 36, "Erstellt mit Disposio", 8, grau: true);

        return seite.AlsPdf("Monatsstatistik " + StatistikTexte.Lang(statistik.Monat) + " - " + statistik.Firmenname);
    }

    /// <summary>
    /// Eine A4-Seite (595 × 842 Punkte, Ursprung unten links) mit den Standardschriften Helvetica und
    /// Helvetica-Bold. Texte werden in Windows-1252 kodiert, damit Umlaute, ß und € richtig erscheinen.
    /// </summary>
    private sealed class PdfSeite
    {
        private static readonly Encoding WinAnsi = WinAnsiKodierung();
        private readonly StringBuilder _inhalt = new();

        public void Text(double x, double y, string text, double groesse, bool fett = false, bool grau = false)
        {
            // BT … ET ist ein Textblock: Farbe, Schrift, Position und der Text selbst.
            _inhalt.Append("BT ").Append(grau ? "0.4 g" : "0 g")
                .Append(fett ? " /F2 " : " /F1 ").Append(Zahl(groesse)).Append(" Tf ")
                .Append(Zahl(x)).Append(' ').Append(Zahl(y)).Append(" Td (").Append(Maskieren(text)).Append(") Tj ET\n");
        }

        public void Linie(double x1, double y1, double x2, double y2, double dicke)
        {
            _inhalt.Append("0.75 G ").Append(Zahl(dicke)).Append(" w ")
                .Append(Zahl(x1)).Append(' ').Append(Zahl(y1)).Append(" m ")
                .Append(Zahl(x2)).Append(' ').Append(Zahl(y2)).Append(" l S\n");
        }

        public byte[] AlsPdf(string titel)
        {
            byte[] inhalt = WinAnsi.GetBytes(_inhalt.ToString());
            var objekte = new List<byte[]>
            {
                Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
                Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
                      "/Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>"),
                Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
                Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
                Verbinden(Ascii($"<< /Length {inhalt.Length} >>\nstream\n"), inhalt, Ascii("\nendstream")),
                Verbinden(Ascii("<< /Title ("), WinAnsi.GetBytes(Maskieren(titel)), Ascii(") /Producer (Disposio) >>"))
            };

            // Aufbau: Kopf, nummerierte Objekte, Verzeichnis mit der Byte-Position jedes Objekts, Schluss.
            using var pdf = new MemoryStream();
            Schreiben(pdf, Ascii("%PDF-1.4\n%"));
            Schreiben(pdf, new byte[] { 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }); // kennzeichnet die Datei als binär
            var positionen = new List<long>();
            for (int i = 0; i < objekte.Count; i++)
            {
                positionen.Add(pdf.Position);
                Schreiben(pdf, Ascii($"{i + 1} 0 obj\n"));
                Schreiben(pdf, objekte[i]);
                Schreiben(pdf, Ascii("\nendobj\n"));
            }

            long verzeichnis = pdf.Position;
            var xref = new StringBuilder($"xref\n0 {objekte.Count + 1}\n0000000000 65535 f \n");
            foreach (long position in positionen)
                xref.Append(position.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            xref.Append($"trailer\n<< /Size {objekte.Count + 1} /Root 1 0 R /Info 7 0 R >>\n")
                .Append($"startxref\n{verzeichnis}\n%%EOF\n");
            Schreiben(pdf, Ascii(xref.ToString()));
            return pdf.ToArray();
        }

        // In PDF-Texten haben \, ( und ) eine Sonderbedeutung und werden mit \ maskiert.
        private static string Maskieren(string text) =>
            text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", " ").Replace("\n", " ");

        private static string Zahl(double wert) => wert.ToString("0.##", CultureInfo.InvariantCulture);

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        private static byte[] Verbinden(params byte[][] teile) => teile.SelectMany(teil => teil).ToArray();

        private static void Schreiben(Stream ziel, byte[] daten) => ziel.Write(daten, 0, daten.Length);

        // Windows-1252 ist in .NET nicht vorinstalliert und wird einmal angemeldet.
        // Zeichen, die es dort nicht gibt, erscheinen als „?“.
        private static Encoding WinAnsiKodierung()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
    }
}
