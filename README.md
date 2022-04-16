
# RepeaterWatchdog
### By M0XDR
## Introduction
Repeater watcher performs ICMP ping commands at set intervals and kills and restarts a process if failures reach a specified threshold. I use this for virtual hosted Wires-X machines that accept incoming USB/IP connections from on-prem RaspberryPis that are connected to HRI200 interface boxes. This means that we do not need Windows PC devices at remote repeater sites.

The intention is that these machines connect via VPN to the internet which solves issues with CGNAT. This utility will test connectivity to multiple public DNS providers and restart a specified process (with the provided command line arguments) if a specified failure threshold is reached.

## Command Line
```
Usage:
  RepeaterWatchdog <restartArguments>... [options]

Arguments:
  <restartArguments>  The arguments which will be passed to the restarted process.

Options:
  -p, --process <process>            The process to kill and restart. [default: OpenVPNConnect]
  -d, --destinations <destinations>  One or more destination IP or addresses to ping. [default:
                                     1.1.1.1|8.8.8.8|208.67.222.222]
  -i, --interval <interval>          The number of seconds between tests. [default: 15]
  -f, --failures <failures>          The number consecutive failures before restarting. [default: 5]
  -t, --timeout <timeout>            The ping timeout in seconds. [default: 3]
  -a, --aux <aux>                    The path to an auxillary process to run each period.
  -s, --skip <skip>                  The number of intervals to skip before running the aux process. [default: 3]
  -x, --auxargs <auxargs>            An argument string for the auxillary process.
  --version                          Show version information
  -?, -h, --help                     Show help and usage information
```
### Example StartWiresX.bat batch file
```
@echo off
echo Waiting 60 seconds to ensure USB devices are connected...
timeout /t 60
echo Starting Wires-X
start C:\Radio\YAESUMUSEN\WIRES-X\YmLAN.exe /new
echo Starting VPN
qprocess "OpenVPNConnect.exe" > NULL
if %ERRORLEVEL% EQU 1 (
  echo VPN NOT Running. Starting...
  "C:\Program Files\OpenVPN Connect\OpenVPNConnect.exe" --opened-at-login --minimize
) else ( 
  echo VPN running. No need to start.
)
echo Starting network watchdog
start C:\Radio\Watchdog\RepeaterWatchdog.exe --opened-at-login --minimize --connect-shortcut=1649759676236 -a "C:\Program Files\MacroCreator\MacroCreator.exe" -x "C:\Radio\Automation\CheckforWiresXWarningDialog.pmc -s1"
```
