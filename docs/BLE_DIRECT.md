# Bluetooth LE and receiver discovery

Windows build V0.16.0 and Android V0.16.0 add authenticated Wi-Fi setup over BLE.

## Connection flow

1. Pair Windows and Android once through AnyDisplay over USB or an existing network, confirming the PIN.
2. In Android enable **Bluetooth-Direktverbindung**. Allow Bluetooth and nearby-device permissions. Android 10–12 also need location permission for scanning.
3. In Windows choose **Direkt-WLAN** and **Verbinden**. Select the paired receiver when more than one is stored.
4. Windows starts the saved AP and advertises the AnyDisplay BLE service while it searches for the receiver.
5. Android authenticates the host through the existing pairing secret, decrypts the AP credentials and requests the exact SSID and BSSID with WifiNetworkSpecifier. Windows uses an authenticated GATT exchange first. If its GATT server cannot advertise, it switches to a time-limited encrypted extended BLE advertisement.
6. Confirm Android's system Wi-Fi dialog when required. Android can remember approval for this specific app and AP.
7. In GATT mode Android reports its new IPv4 endpoint through the authenticated BLE channel. In beacon mode Windows discovers the endpoint through mDNS. Windows checks the route goes through its Wi-Fi Direct adapter and connects using the existing encrypted AnyDisplay protocol.

The Android connection foreground service owns the filtered BLE scan and local-only network request. Keep the service running. Android and vendor power-management policies can affect discovery while the tablet sleeps. Stopping the service releases its network request. Ending the Windows connection stops BLE advertising and the AP. A temporarily dropped BLE link after credentials are received can complete discovery through mDNS.

An existing Windows Wi-Fi Direct, Miracast or mobile-hotspot session may occupy the radio. BLE setup requires the AnyDisplay AP to start successfully. Current builds report this conflict.

## Existing-network mode

**Verbinden** searches automatically through mDNS and UDP. Manually entered addresses connect directly. **Suchen** refreshes the list at any time. One receiver connects automatically. Multiple receivers open a list with device names and addresses. Multiple addresses belonging to one device are grouped and an address on a directly attached subnet is preferred. The address field also accepts manual input. UDP discovery broadcasts on local IPv4 subnets and sends unicast queries to previously connected addresses. If Tailscale is installed, Windows also reads its local peer list and queries those VPN addresses directly. Tailscale is optional. Both endpoints need the new APK for the UDP exchange.

Selecting an address while streaming stages the connection change. **Verbindung neu aufbauen** confirms that change. Picture options continue to apply to the current stream immediately.

## UDP discovery

Android listens on UDP 47820 while its connection service runs. Windows sends ADQ1 (4 bytes) and a random nonce (16). A response contains ADR1 (4), the same nonce (16), device_id (16), TCP port (2, big endian), UTF-8 name length (2, big endian) and name (up to 240). Android rate-limits replies. Windows verifies the nonce, lengths and port before adding the receiver to its existing name/address list. Discovery does not authenticate a device. The normal PIN and paired-key handshake authenticate the stream.

## BLE wire protocol v1

All lengths are bytes. UUIDs have prefix 7162447a-9075-4d55-a17c-7ec7b1c001.

- Service ends in 00.
- Identity characteristic 01 is readable and returns host_id (16) and a fresh server nonce (16).
- Request characteristic 02 is writable and accepts device_id (16), client nonce (16), request tag (32).
- Offer characteristic 03 is readable after successful authentication. It contains IV (16), AES-256-CBC/PKCS7 ciphertext and an HMAC-SHA256 tag (32).
- Ready characteristic 04 is writable and accepts IPv4 (4), port (2, big endian), ready tag (32).

The client negotiates ATT MTU of at least 185. Each GATT session has its own challenge and response. Windows accepts the selected paired device only. A new server instance and a new identity read both issue a fresh server nonce.

Derive separate keys with HMAC-SHA256(pair_secret, UTF8(label)).

- Encryption label is AnyDisplay BLE encryption v1.
- Authentication label is AnyDisplay BLE authentication v1.

Tags are HMAC-SHA256(authentication_key, concatenated fields).

- Request covers UTF8(request), host_id, server_nonce, device_id, client_nonce.
- Offer covers UTF8(offer), server_nonce, client_nonce, IV and ciphertext.
- Ready covers UTF8(ready), server_nonce, client_nonce, IPv4 and port.

The encrypted plaintext is BSSID (6), SSID length (1), UTF8 SSID, password length (1), UTF8 WPA2 password. Verify the offer tag before decrypting. Pairing secrets stay on the paired devices. The extended beacon contains encrypted Wi-Fi credentials. Tests use shared fixed request/ready vectors, a known encrypted offer, tampered ciphertext and a different challenge.


## Extended BLE advertisement fallback

This fallback runs when Windows GATT advertising fails and its Bluetooth adapter supports extended advertisements. The Intel adapter on the development PC successfully advertised this packet through the production code.

Manufacturer ID 0xffff carries an ADW1 marker (4), host_id (16), timestamp in Unix milliseconds (8, big endian), random bytes (8), and the encrypted offer described above. The timestamp and random bytes form server_nonce. The selected Android device_id takes the place of client_nonce in the offer tag. Android verifies the tag using the existing per-host pairing secret before decrypting.

Accept timestamps from the preceding 120 seconds and at most 30 seconds into the future. Android persists the newest accepted timestamp per host before requesting Wi-Fi. Equal or older timestamps are ignored across app restarts. Keep the clocks on both devices current. Rejected or cancelled Wi-Fi requests can be retried with a fresh Windows connection attempt.

The scanner includes legacy GATT service advertisements and extended manufacturer advertisements. Beacon mode discovers the Android endpoint through mDNS after the local-only network becomes available.

## Test builds and signing

The Android APK uses the build runner’s temporary debug signer. Android may require removing an installation signed by another build or the upstream maintainer. Removing it also removes its pairing secrets. Pair the new installation again. A persistent signing key was not configured.

The Windows package contains AnyDisplay-Start.exe and anydisplay-win.exe. Keep both together.

## Hardware test

- Confirm permissions and initial pairing, then start the Windows direct connection.
- Confirm the first Android Wi-Fi request and verify both devices log the expected direct endpoint.
- Disconnect and reconnect, checking that saved approval works and the AP disappears after disconnect.
- Repeat after restarting both apps and after toggling Bluetooth.
- Check rejection of the wrong receiver and cancellation of the Android Wi-Fi dialog.
- In Network mode start two Android receivers and verify their names and addresses appear.

Sources include [Android Wi-Fi Network Request API](https://developer.android.com/develop/connectivity/wifi/wifi-bootstrap), [Android Bluetooth permissions](https://developer.android.com/develop/connectivity/bluetooth/bt-permissions) and [Windows GATT server](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/gatt-server).
