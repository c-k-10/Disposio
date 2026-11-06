import 'package:flutter/material.dart';

/// Farben und Formen aus den Fahrer-Wireframes (M01 bis M15).
class DisposioFarben {
  static const dunkelblau = Color(0xFF263749); // Hauptfarbe, z. B. "Fahrt starten"
  static const hintergrund = Color(0xFFF4F5F9);
  static const karte = Colors.white;
  static const rand = Color(0xFFD5D9E0);
  static const textHaupt = Color(0xFF1F2933);
  static const textLeise = Color(0xFF6B7280);
  static const statusBox = Color(0xFFF0F1F3); // graue Statuszeile "Bereit · GPS aus"
}

class DisposioTheme {
  static ThemeData get hell {
    final basis = ColorScheme.fromSeed(
      seedColor: DisposioFarben.dunkelblau,
      primary: DisposioFarben.dunkelblau,
      onPrimary: Colors.white,
      surface: DisposioFarben.karte,
    );

    final eckig = RoundedRectangleBorder(borderRadius: BorderRadius.circular(4));

    return ThemeData(
      useMaterial3: true,
      colorScheme: basis,
      scaffoldBackgroundColor: DisposioFarben.hintergrund,
      appBarTheme: const AppBarTheme(
        backgroundColor: Colors.white,
        foregroundColor: DisposioFarben.textHaupt,
        elevation: 0,
        titleTextStyle: TextStyle(
          color: DisposioFarben.textHaupt,
          fontSize: 16,
          fontWeight: FontWeight.w700,
          letterSpacing: 0.5,
        ),
      ),
      cardTheme: CardThemeData(
        color: DisposioFarben.karte,
        elevation: 0,
        margin: const EdgeInsets.symmetric(vertical: 6),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(4),
          side: const BorderSide(color: DisposioFarben.rand),
        ),
      ),
      // Grosse Buttons, damit der Fahrer sie sicher trifft
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: DisposioFarben.dunkelblau,
          foregroundColor: Colors.white,
          minimumSize: const Size.fromHeight(52),
          shape: eckig,
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          foregroundColor: DisposioFarben.textHaupt,
          minimumSize: const Size(0, 52),
          side: const BorderSide(color: DisposioFarben.rand),
          shape: eckig,
        ),
      ),
      inputDecorationTheme: const InputDecorationTheme(
        filled: true,
        fillColor: Colors.white,
        border: OutlineInputBorder(),
        enabledBorder: OutlineInputBorder(
          borderSide: BorderSide(color: DisposioFarben.rand),
        ),
      ),
      navigationBarTheme: const NavigationBarThemeData(
        backgroundColor: Colors.white,
        indicatorColor: DisposioFarben.statusBox,
      ),
    );
  }
}
