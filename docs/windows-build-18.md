AnyDisplay – Windows Build 18

Änderung gegenüber Build 17:
- Keine pauschale Sperre wegen aktiver Microsoft-Wi-Fi-Direct-Adapter.
- Jede AnyDisplay-Direkt-WLAN-Sitzung gehört einem eigenen Hilfsprozess.
- Trennen stoppt nur diesen eigenen Prozess; Wiederverbinden wartet auf dessen Ende.
- Geschlossener Steuerkanal (auch beim Absturz des Hauptprogramms) beendet den Hilfsprozess.
- Echte Windows-Konflikte bleiben sichtbar. Fremde Hotspots/Adapter werden nicht beendet oder deaktiviert.
- Bluetooth und der Videostream verwenden den ermittelten Sitzungsadapter statt des ersten passenden Adapters.

Start:
AnyDisplay-Start.exe öffnen. Bei laufender alter Version „Neu starten“ bestätigen, sobald der bisherige Stream beendet werden darf.
Beide EXEs müssen in diesem Ordner bleiben. Die vorhandene Android-App 0.16.0 bleibt passend; keine Neuinstallation und keine neue Kopplung erforderlich.

Lokal geprüft:
- Kompilierung und Launcher-Smoke-Test inklusive Adapterauswahl, Videooptionen, Profil und BLE-Protokollvektoren.
- Drei aufeinanderfolgende Start/Stop-Zyklen des echten Controllers gegen isolierte Testprozesse.
- Geschlossener Steuerkanal, Konfliktstatus und erzwungenes Ende eines nicht reagierenden eigenen Hilfsprozesses.
- Stream-EXE ist identisch zu Build 17 (SHA-256 geprüft).
Diese Prozesstests öffnen kein WLAN. Der neue native WLAN-Lebenszyklus muss am Tablet noch getestet werden.

Nächster gemeinsamer Test:
Direkt-WLAN verbinden, auf Android gegebenenfalls die WLAN-Freigabe bestätigen, kurz streamen, unter Windows trennen und erneut verbinden.
Bitte Fehlertext und Build-Anzeige melden, falls es wieder scheitert.
