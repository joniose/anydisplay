//! `anydisplay-win`: command-line AnyDisplay host.

use anyhow::{anyhow, bail};
use anydisplay::adb::Adb;
use anydisplay::pairing::PairingStore;
use anydisplay::platform::Platform;
use anydisplay::proto::messages::{bye, Codec, DEFAULT_PORT};
use anydisplay::session::{self, SessionConfig, SessionHandle, VideoSettings};
use anydisplay::{info, warn};
use std::io::{BufRead, Write};
use std::sync::{mpsc, Mutex};
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;
use std::time::Duration;

const USAGE: &str = "\
usage:
  anydisplay-win list [--seconds N]              monitors, USB (adb) and Wi-Fi (mDNS) clients, paired devices
  anydisplay-win usb [SERIAL] [options]          connect to a USB client through adb forward
  anydisplay-win connect HOST[:PORT] [options]   connect to a client over the network (default port 47820)

options:
  --monitor N        capture monitor N from `list` (default: the virtual display if installed,
                     else the primary monitor)
  --codec h264|hevc  preferred codec (used when the client can decode it)
  --fps 60|30|15|variable  stream rate ceiling; variable uses the negotiated rate and skips still frames
  --display mirror|extend  mirror the main monitor or require a virtual display
  --scale full|three-quarter|half|quarter  scale the source monitor in both dimensions
  --size WxH         override the requested stream size (alternative to --scale)
  --bitrate K|auto   set bitrate in kbps or let the host choose from stream settings
  -y, --yes          accept the pairing code without asking
  --seconds N        end the session N seconds after streaming starts
  --no-clipboard     do not share the clipboard
  --no-audio         never stream audio
  --test-source      stream an ffmpeg test pattern instead of the desktop (any OS)
  -v, --verbose      debug logging

environment: ANYDISPLAY_STORE (pairing store path), ANYDISPLAY_ADB (adb path), ANYDISPLAY_FFMPEG (ffmpeg path)";

#[derive(Default)]
struct Opts {
    positional: Vec<String>,
    monitor: Option<usize>,
    codec: Option<Codec>,
    size: Option<(u16, u16)>,
    fps: Option<u16>,
    mirror: Option<bool>,
    scale: Option<u32>,
    bitrate: Option<u32>,
    yes: bool,
    seconds: Option<f64>,
    clipboard: bool,
    audio: bool,
    test_source: bool,
    gui: bool,
}

fn parse(args: &[String]) -> anyhow::Result<Opts> {
    let mut o = Opts {
        clipboard: true,
        audio: true,
        ..Default::default()
    };
    let mut it = args.iter();
    while let Some(a) = it.next() {
        let mut value = |name: &str| {
            it.next()
                .cloned()
                .ok_or_else(|| anyhow!("{name} needs a value"))
        };
        match a.as_str() {
            "-y" | "--yes" => o.yes = true,
            "-v" | "--verbose" => anydisplay::log::set_verbose(true),
            "--no-clipboard" => o.clipboard = false,
            "--no-audio" => o.audio = false,
            "--test-source" => o.test_source = true,
            "--gui" => o.gui = true,
            "--monitor" => o.monitor = Some(value("--monitor")?.parse()?),
            "--seconds" => o.seconds = Some(value("--seconds")?.parse()?),
            "--fps" => {
                o.fps = match value("--fps")?.as_str() {
                    "60" => Some(60),
                    "30" => Some(30),
                    "15" => Some(15),
                    "variable" => None,
                    _ => bail!("expected --fps 60, 30, 15, or variable"),
                };
            }
            "--display" => {
                o.mirror = Some(match value("--display")?.as_str() {
                    "mirror" => true,
                    "extend" => false,
                    _ => bail!("expected --display mirror or extend"),
                });
            }
            "--scale" => {
                o.scale = Some(match value("--scale")?.as_str() {
                    "full" => 100,
                    "three-quarter" => 75,
                    "half" => 50,
                    "quarter" => 25,
                    _ => bail!("expected --scale full, three-quarter, half, or quarter"),
                });
            }
            "--size" => {
                let size = value("--size")?;
                let (w, h) = size.split_once('x').ok_or_else(|| anyhow!("expected WxH"))?;
                let (w, h): (u16, u16) = (w.parse()?, h.parse()?);
                if w < 320 || h < 240 || w % 2 != 0 || h % 2 != 0 {
                    bail!("stream size must be even and at least 320x240");
                }
                o.size = Some((w, h));
            }
            "--bitrate" => {
                let bitrate = value("--bitrate")?;
                o.bitrate = Some(if bitrate == "auto" {
                    0
                } else {
                    let kbps = bitrate.parse()?;
                    if !(1..=80_000).contains(&kbps) {
                        bail!("bitrate must be between 1 and 80000 kbps or auto");
                    }
                    kbps
                });
            }
            "--codec" => {
                o.codec = Some(match value("--codec")?.to_ascii_lowercase().as_str() {
                    "h264" | "avc" => Codec::H264,
                    "hevc" | "h265" => Codec::Hevc,
                    other => bail!("unknown codec {other} (h264 or hevc)"),
                })
            }
            s if s.starts_with('-') => bail!("unknown option {s}"),
            _ => o.positional.push(a.clone()),
        }
    }
    if o.gui && o.yes {
        bail!("GUI pairing requires PIN confirmation");
    }
    if o.scale.is_some() && o.size.is_some() {
        bail!("choose either --scale or --size");
    }
    Ok(o)
}

fn host_name() -> String {
    if let Ok(n) = std::env::var("COMPUTERNAME") {
        return n;
    }
    std::process::Command::new("hostname")
        .output()
        .ok()
        .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_string())
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "AnyDisplay host".into())
}

fn make_platform(o: &Opts) -> anyhow::Result<Box<dyn Platform>> {
    if o.test_source || cfg!(not(windows)) {
        if cfg!(not(windows)) && !o.test_source {
            info!("not on Windows: streaming the ffmpeg test pattern (--test-source)");
        }
        return Ok(Box::new(anydisplay::testsrc::TestSource::default()));
    }
    #[cfg(windows)]
    return Ok(Box::new(
        anydisplay::win::WinPlatform::new(o.monitor)?,
    ));
    #[cfg(not(windows))]
    unreachable!()
}

fn prompt() -> session::SasPrompt {
    Arc::new(|code: &str| {
        eprint!("Pairing code {code}. Does the device show the same code? [y/N] ");
        let _ = std::io::stderr().flush();
        let mut line = String::new();
        let _ = std::io::stdin().read_line(&mut line);
        matches!(line.trim().to_ascii_lowercase().as_str(), "y" | "yes")
    })
}

fn parse_video_settings(line: &str) -> anyhow::Result<VideoSettings> {
    let args: Vec<_> = line.split_ascii_whitespace().map(String::from).collect();
    let fields = ["--codec", "--fps", "--display", "--scale", "--bitrate"];
    if args.len() != fields.len() * 2 || !args.iter().step_by(2).map(String::as_str).eq(fields) {
        bail!("invalid launcher video settings");
    }
    let o = parse(&args)?;
    Ok(VideoSettings {
        codec: o.codec,
        size: o.size,
        fps: o.fps,
        bitrate: o.bitrate,
        mirror: o.mirror,
        scale: o.scale,
    })
}

// Private line-based channel for the desktop launcher. Losing the launcher ends the session.
fn gui_prompt(handle: SessionHandle) -> session::SasPrompt {
    let (send, recv) = mpsc::channel();
    std::thread::spawn(move || {
        for line in std::io::stdin().lock().lines().map_while(Result::ok) {
            if line == "stop" {
                handle.stop(bye::SHUTDOWN);
                return;
            }
            if let Some(args) = line.strip_prefix("video ") {
                match parse_video_settings(args) {
                    Ok(settings) => handle.set_video(settings),
                    Err(e) => warn!("video settings rejected: {e}"),
                }
            } else if (line == "reject" || line.starts_with("confirm "))
                && send.send(line).is_err()
            {
                return;
            }
        }
        handle.stop(bye::SHUTDOWN);
    });
    let recv = Mutex::new(recv);
    Arc::new(move |code| {
        eprintln!("ANYDISPLAY_PIN {code}");
        recv.lock()
            .unwrap()
            .recv_timeout(Duration::from_secs(60))
            .is_ok_and(|answer| answer == format!("confirm {code}"))
    })
}

fn run_session(addr: std::net::SocketAddr, o: &Opts, adb_forwarded: bool) -> anyhow::Result<bool> {
    let mut store = PairingStore::open(PairingStore::default_path())?;
    let mut platform = make_platform(o)?;
    let cfg = SessionConfig {
        host_name: host_name(),
        auto_confirm: o.yes,
        adb_forwarded,
        codec: o.codec,
        size: o.size,
        fps: o.fps,
        bitrate: o.bitrate,
        mirror: o.mirror,
        scale: o.scale,
        clipboard: o.clipboard,
        audio: o.audio,
        duration: o.seconds.map(Duration::from_secs_f64),
    };
    let handle = SessionHandle::default();
    let h = handle.clone();
    let presses = AtomicU32::new(0);
    ctrlc::set_handler(move || {
        if presses.fetch_add(1, Ordering::SeqCst) > 0 {
            std::process::exit(130);
        }
        h.stop(bye::SHUTDOWN);
    })?;
    let pairing = if o.gui { gui_prompt(handle.clone()) } else { prompt() };
    let summary = session::run(addr, &mut store, platform.as_mut(), &cfg, pairing, &handle)?;
    Ok(summary.error.is_none())
}

// Machine-readable DNS-SD results for the Windows launcher. The launcher filters by route.
fn discover(o: &Opts) -> anyhow::Result<()> {
    let store = PairingStore::open(PairingStore::default_path())?;
    for device in anydisplay::discovery::browse(Duration::from_secs_f64(o.seconds.unwrap_or(2.0)))? {
        let id = device
            .device_id
            .as_deref()
            .filter(|id| id.len() == 32 && id.bytes().all(|byte| byte.is_ascii_hexdigit()))
            .unwrap_or("-")
            .to_ascii_lowercase();
        let paired = store.devices().contains_key(&id);
        for address in device.addrs.into_iter().filter(|address| address.is_ipv4()) {
            println!("PEER {address} {id} {}", u8::from(paired));
        }
    }
    Ok(())
}

fn list(o: &Opts) -> anyhow::Result<()> {
    #[cfg(windows)]
    {
        println!("monitors:");
        for (i, m) in anydisplay::win::monitors::list()?.iter().enumerate() {
            println!("  {i}: {m}");
        }
    }
    match Adb::locate() {
        Some(adb) => {
            let devices = adb.devices()?;
            println!(
                "usb ({}):{}",
                adb.path.display(),
                if devices.is_empty() { " (none)" } else { "" }
            );
            for d in devices {
                println!("  {}  {}", d.serial, d.model.unwrap_or_default());
            }
        }
        None => println!("usb: adb not found (PATH, Android SDK, or ANYDISPLAY_ADB)"),
    }
    let seconds = o.seconds.unwrap_or(3.0);
    let found = anydisplay::discovery::browse(Duration::from_secs_f64(seconds))?;
    println!(
        "wifi (_anydisplay._tcp, {seconds} s):{}",
        if found.is_empty() { " (none)" } else { "" }
    );
    for d in found {
        let addrs: Vec<String> = d.addrs.iter().map(|a| a.to_string()).collect();
        println!(
            "  {}  id={}  {}",
            d.name,
            d.device_id.as_deref().unwrap_or("?"),
            addrs.join(" ")
        );
    }
    let store = PairingStore::open(PairingStore::default_path())?;
    println!(
        "paired ({}):{}",
        store.path().display(),
        if store.devices().is_empty() {
            " (none)"
        } else {
            ""
        }
    );
    for (id, d) in store.devices() {
        println!("  {id}  {}  paired {}", d.name, d.paired_at);
    }
    Ok(())
}

fn main() {
    #[cfg(windows)]
    anydisplay::win::init_process();
    let args: Vec<String> = std::env::args().skip(1).collect();
    let command = args.first().cloned().unwrap_or_default();
    let result = parse(args.get(1..).unwrap_or_default()).and_then(|o| match command.as_str() {
        "list" => list(&o).map(|_| true),
        "discover" => discover(&o).map(|_| true),
        "connect" => {
            let target = o
                .positional
                .first()
                .ok_or_else(|| anyhow!("connect needs HOST[:PORT]\n\n{USAGE}"))?;
            run_session(session::resolve(target, DEFAULT_PORT)?, &o, false)
        }
        "usb" => {
            let adb = Adb::locate()
                .ok_or_else(|| anyhow!("adb not found (PATH, Android SDK, or set ANYDISPLAY_ADB)"))?;
            let serial = match o.positional.first() {
                Some(s) => s.clone(),
                None => {
                    let devices = adb.devices()?;
                    match devices.as_slice() {
                        [one] => one.serial.clone(),
                        [] => bail!("no USB device with debugging authorized"),
                        many => bail!(
                            "several devices, pick one: {}",
                            many.iter()
                                .map(|d| d.serial.as_str())
                                .collect::<Vec<_>>()
                                .join(" ")
                        ),
                    }
                }
            };
            let port = adb.forward(&serial, DEFAULT_PORT)?;
            info!("adb forward tcp:{port} -> {serial}:{DEFAULT_PORT}");
            let r = run_session(std::net::SocketAddr::from(([127, 0, 0, 1], port)), &o, true);
            adb.remove_forward(&serial, port);
            r
        }
        "" | "help" | "-h" | "--help" => {
            println!("{USAGE}");
            Ok(true)
        }
        other => bail!("unknown command {other}\n\n{USAGE}"),
    });
    match result {
        Ok(true) => {}
        Ok(false) => std::process::exit(1),
        Err(e) => {
            warn!("{e:#}");
            std::process::exit(2);
        }
    }
}
