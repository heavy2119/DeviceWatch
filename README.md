# DeviceWatch

Windows에서 짧게 지나가는 USB, HID, PnP 연결 변화를 파일로 남기는 기록 프로그램입니다.

A Windows recorder for brief USB, HID, and PnP connection changes.

[한국어](#한국어) · [English](#english)

## 한국어

DeviceWatch.Probe는 장치 연결이 잠깐 끊겼는지 나중에 확인할 수 있도록, 관찰한 내용을 한 줄씩 JSONL로 저장합니다. 시뮬레이터 휠의 포스 피드백이 순간적으로 빠지거나, HID 장치가 잠깐 사라지는 것처럼 화면에서 놓치기 쉬운 변화를 대상으로 합니다.

이 저장소에 들어 있는 것은 수집과 로그 확인용 Probe입니다. 트레이, 로그인 자동 시작, SQLite, 원인 판정은 포함하지 않습니다.

### 기록하는 것

항상 켜 두는 수집은 다음입니다.

- 모든 장치 인스턴스에 대한 `CM_Register_Notification`
- USB 장치, USB 허브, USB 호스트 컨트롤러, HID 인터페이스 알림
- 등록 직후의 현재 장치와 인터페이스 스냅샷
- 숨은 창으로 받는 `WM_DEVICECHANGE`
- 사용 가능한 경우 Kernel-PnP 이벤트 로그

`USBHUB3 ETW`를 켜면 `Microsoft-Windows-USB-USBHUB3`의 Default 키워드만 추가로 기록합니다. 포트 상태처럼 더 촘촘한 허브 기록을 보기 위한 선택 기능입니다. 버스 데이터와 StateMachine 키워드는 켜지 않습니다. 관리자 권한이 없으면 이 추적만 실패로 남고, 나머지 수집은 계속됩니다.

한 줄은 물리적인 분리 한 번이 아니라, 소스 하나가 관찰한 기록 한 건입니다. 같은 변화가 여러 소스에 나타나면 줄도 여러 개로 남습니다. 원본(`raw`), 그 순간 조회한 값(`queried`), 이전에 알고 있던 값(`lastKnown`)은 서로 덮어쓰지 않습니다.

### 보여 주는 것

기록 창은 시간, 소스, 동작, 장치를 짧은 목록으로 보여 줍니다. 스냅샷은 파일에만 남고, `DBT_DEVNODES_CHANGED`는 파일에는 저장하지만 실시간 목록에서는 숨깁니다. 장치 식별자가 없는 방송이기 때문입니다.

끊김 의심은 같은 장치 키에서 제거 기록이 있을 때만 만듭니다. 제거 뒤에 다시 연결 기록이 있으면 `제거 뒤 다시 연결`, 제거만 있으면 `제거 기록만 있음`입니다. 의심 목록은 그 근거 줄과 조회 시각을 함께 보여 줍니다. 케이블, 허브, 드라이버 고장이라고 결론내지 않습니다. 서로 다른 장치 키는 합치지 않습니다.

Kernel-PnP 이벤트 번호와 USBHUB3의 `EVENT_*` 기록은 연결이 끊겼다는 근거로 쓰지 않습니다.

### 한계

PnP 알림은 장치 노드와 인터페이스의 수명을 보여 줍니다. 장치가 그대로 있는 동안의 USB 전송 실패, 포스 피드백 패킷 손실, 선택적 절전은 끊김처럼 보이지 않을 수 있습니다. 아주 짧은 접촉 불량이 모두 기록된다고 보지 않습니다.

아래 하드웨어 확인은 아직 이 저장소의 통과 조건으로 완료된 기록이 아닙니다.

| 확인 | 내용 |
| --- | --- |
| A | 물리적으로 뽑기 |
| B | 다시 연결 |
| C | HID 인터페이스만 제거 |
| D | USB 부모는 유지된 채 HID만 변경 |
| E | 허브 분리 |
| F | 장치 관리자에서 다시 시작 |
| G | 사용 안 함 / 사용 |
| H | 짧은 간격의 반복 분리 |

### 요구 사항

- 64비트 Windows
- .NET 8 이상 Windows Desktop 런타임. 프로젝트는 `RollForward`가 `LatestMajor`라서 더 새로운 데스크톱 런타임에서도 실행됩니다.
- USBHUB3 ETW를 실제로 열려면 관리자 권한, Performance Log Users, 또는 서비스 계정이 필요합니다.

### 빌드

저장소 루트에서 실행합니다.

```powershell
dotnet build src/DeviceWatch.Probe/DeviceWatch.Probe.csproj -c Release
```

출력 폴더의 다음 네 파일을 함께 둡니다.

- `DeviceWatch.Probe.exe`
- `DeviceWatch.Probe.dll`
- `DeviceWatch.Probe.runtimeconfig.json`
- `DeviceWatch.Probe.deps.json`

`.pdb`는 실행에 필요 없습니다.

### 실행

```powershell
DeviceWatch.Probe.exe
DeviceWatch.Probe.exe --etw
DeviceWatch.Probe.exe --duration 30 --etw
DeviceWatch.Probe.exe --output D:\traces\probe.jsonl
DeviceWatch.Probe.exe --inspect D:\traces\probe.jsonl
```

`기록 시작`은 실행 파일 옆 `logs` 폴더에 `probe-yyyyMMdd-HHmmss.jsonl`을 새로 만듭니다. 같은 파일을 덮어쓰지 않습니다. `--output`을 주면 그 파일을 쓰고, 로그를 여는 창은 그 파일이 있는 폴더에서 시작합니다.

`--duration`은 지정한 초 동안 기록한 뒤 창을 닫습니다. `--inspect`는 저장된 JSONL의 줄 수, 소스별 변화, 끊김 의심을 콘솔에 요약합니다. 기록 중인 파일도 열 수 있습니다.

창을 닫으면 수집도 멈춥니다. Probe는 창과 수집이 한 프로세스입니다.

### 로그 형식

스키마 버전 1입니다. `recordType`은 `event`, `snapshot`, `collectorStatus` 중 하나입니다.

| 필드 | 의미 |
| --- | --- |
| `probeSequence` | 이 파일 안에서의 순서 |
| `source` | `CmDeviceInstance`, `CmDeviceInterface`, `WmDeviceChange`, `KernelPnp`, `UsbHub3Etw`, `Snapshot` 등 |
| `observedAtUtc` | Probe가 그 줄을 관찰한 UTC 시각 |
| `observedQpc` | 같은 관찰의 `QueryPerformanceCounter` 값 |
| `sourceTimeUtc` | 원본 이벤트에 시각이 있을 때만 채움. CM과 WM 기록은 `null` |
| `action` | 소스의 동작 이름 |
| `raw` | 콜백이나 이벤트에서 바로 복사한 값 |
| `queried` | 그 줄 처리 중 조회한 장치 속성. ETW 줄은 장치를 다시 조회하지 않음 |
| `lastKnown` | 이번 조회 전에 캐시에 있던 값 |

VID와 PID는 하드웨어 ID의 `VID_xxxx` / `PID_xxxx`에서만 읽습니다. 인스턴스 ID 문자열만 보고 만들지 않습니다. 조회에 실패하면 실패 코드를 함께 남깁니다.

로그에는 장치 인스턴스 ID, 이름, 부모, VID, PID가 들어갑니다. 개인 장비 정보가 되므로 기록 파일을 저장소에 넣지 않는 것이 좋습니다.

## English

DeviceWatch.Probe writes one JSONL line for each observed USB, HID, and PnP change so a short disconnect can be checked later. It is aimed at events that are easy to miss on screen, such as a brief force-feedback drop or a HID device that disappears for a moment.

This repository contains the Probe used to collect and review those logs. It does not include a tray icon, login autostart, SQLite storage, or a fault verdict.

### What it records

These collectors stay on for a recording:

- `CM_Register_Notification` for every device instance
- interface notifications for USB devices, USB hubs, USB host controllers, and HID
- a snapshot of present devices and interfaces taken after registration
- `WM_DEVICECHANGE` from a hidden window
- the Kernel-PnP event log when those channels are available

Checking `USBHUB3 ETW` adds only the Default keyword of `Microsoft-Windows-USB-USBHUB3`. It is an optional, finer hub-port trace. Bus-data and StateMachine keywords stay off. Without administrator rights that trace is recorded as a failure and the other collectors continue.

One line is one observation from one source, not one physical unplug. The same change seen by several sources stays as several lines. The original payload (`raw`), the properties read for that line (`queried`), and the previously known values (`lastKnown`) are kept separate.

### What it shows

The recording window lists time, source, action, and device. Snapshots stay in the file. `DBT_DEVNODES_CHANGED` is stored but hidden in the live list, because that broadcast has no device identity.

A disconnect suspicion is created only when the same device key has a removal record. A later arrival on that key is labeled `제거 뒤 다시 연결` (removed, then connected again). A removal with no return is labeled `제거 기록만 있음` (removal recorded only). The list shows the evidence lines and whether each name, VID/PID, or parent came from the current query or from an earlier one. It does not decide that a cable, hub, or driver failed. Different device keys are not merged.

Kernel-PnP event IDs and USBHUB3 `EVENT_*` rows are not treated as disconnect evidence.

### Limits

PnP notifications describe devnode and interface lifetime. A USB transfer failure, lost force-feedback packet, or selective suspend can happen while the devnode stays started, and then no removal is recorded. A recording from this program does not mean every brief contact fault was seen.

These hardware checks are the intended manual tests. This repository does not contain a completed passing record for them.

| Check | Action |
| --- | --- |
| A | Physical unplug |
| B | Reconnect |
| C | HID interface removal only |
| D | HID change while the USB parent stays |
| E | Hub unplug |
| F | Restart in Device Manager |
| G | Disable and enable |
| H | Short repeated disconnects |

### Requirements

- 64-bit Windows
- The .NET 8 Windows Desktop runtime or a newer desktop runtime. The project sets `RollForward` to `LatestMajor`.
- Administrator rights, membership in Performance Log Users, or a service account are required to open the USBHUB3 ETW session.

### Build

From the repository root:

```powershell
dotnet build src/DeviceWatch.Probe/DeviceWatch.Probe.csproj -c Release
```

Keep these four files together:

- `DeviceWatch.Probe.exe`
- `DeviceWatch.Probe.dll`
- `DeviceWatch.Probe.runtimeconfig.json`
- `DeviceWatch.Probe.deps.json`

The `.pdb` file is not required to run the program.

### Run

```powershell
DeviceWatch.Probe.exe
DeviceWatch.Probe.exe --etw
DeviceWatch.Probe.exe --duration 30 --etw
DeviceWatch.Probe.exe --output D:\traces\probe.jsonl
DeviceWatch.Probe.exe --inspect D:\traces\probe.jsonl
```

`기록 시작` (Start recording) creates a new `probe-yyyyMMdd-HHmmss.jsonl` file in the `logs` folder next to the executable. It does not overwrite an existing file. `--output` writes that file instead, and the open dialog starts in that file's folder.

`--duration` records for the given number of seconds and then closes the window. `--inspect` prints a summary of line counts, changes by source, and disconnect suspicions. A file can be read while it is still being written.

Closing the window stops collection. The Probe keeps the window and the collectors in one process.

### Log format

The schema version is 1. `recordType` is `event`, `snapshot`, or `collectorStatus`.

| Field | Meaning |
| --- | --- |
| `probeSequence` | Order within the file |
| `source` | `CmDeviceInstance`, `CmDeviceInterface`, `WmDeviceChange`, `KernelPnp`, `UsbHub3Etw`, `Snapshot`, and related status sources |
| `observedAtUtc` | UTC time when the Probe observed the line |
| `observedQpc` | `QueryPerformanceCounter` value for that same observation |
| `sourceTimeUtc` | Set only when the original event has a timestamp. CM and WM rows use `null` |
| `action` | Action name from that source |
| `raw` | Values copied directly from the callback or event |
| `queried` | Device properties read while handling the line. ETW rows do not query the device again |
| `lastKnown` | Values already in the cache before this query |

VID and PID are read only from hardware IDs that contain `VID_xxxx` and `PID_xxxx`. They are not parsed out of an opaque instance ID. A failed query keeps its failure code.

Logs contain device instance IDs, names, parents, VID, and PID. Treat them as private and keep recording files out of the repository.
