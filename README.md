# SPD Scheduler

<img src="assets/icon.png" width="112" alt="Circuit board and clock icon">

Windows 트레이에서 PowerSI SPD 파일의 순차 실행과 예약 실행을 관리합니다. C# WinForms / .NET 10, Windows 10·11 x64를 대상으로 합니다.

**현재 상태: 검증용 빌드.** 실제 PowerSI 2025.1 GUI 연동은 별도 허용 PC에서 검증해야 합니다. `-tcl` GUI 실행, 각 저장 모드의 시작·완료·저장·종료를 확인하기 전에는 v0.1.0 정식 릴리스를 게시하지 않습니다. [검증 절차](docs/VALIDATION.md)를 참고하세요.

## 사용법

1. 설치 후 시작 메뉴에서 SPD Scheduler를 엽니다. 평소에는 트레이에 있으며, 아이콘을 더블클릭하면 목록이 열립니다. 창의 X는 숨기기입니다. 앱 종료는 트레이 메뉴의 **종료**를 사용합니다.
2. **설정**에서 자동 탐색된 PowerSI 실행 경로를 확인합니다. 시뮬레이션 설정은 SPD에 미리 저장되어 있어야 합니다.
3. SPD 파일을 목록에 드롭하거나 **파일 추가**로 선택합니다. 드롭으로 전달된 순서가 유지됩니다. 긴 파일명은 좌우로 자동 이동하며, 툴팁과 우클릭 **전체 경로 복사**로 전체 경로를 볼 수 있습니다.
4. 필요한 행에 **예약**을 설정하고 **시작**을 누릅니다. 예약 시간이 지난 작업이 먼저 실행되며, 같은 예약 시간이면 등록 순서입니다. 미래 예약은 일반 대기 작업을 막지 않습니다.
5. **일시 중지**는 다음 작업 시작만 막습니다. 현재 시뮬레이션의 완료 확인·저장·정리는 계속됩니다.

앱 재시작 시 큐는 복구되지만 자동 진행은 일시정지됩니다. 기존 실행이 있으면 먼저 연결하여 확인합니다. `확인 필요` 항목은 PowerSI를 직접 확인한 후 관련 프로세스의 종료가 확인되어야 재시도하거나 건너뛸 수 있습니다. 외부 PowerSI는 저장하거나 종료하지 않습니다.

예약은 입력한 로컬 날짜·시각의 UTC 오프셋 및 시간대 ID를 보존하는 일회성 예약입니다. 이후 Windows 시간대를 바꾸어도 실행할 절대 시각은 바뀌지 않습니다. 절전에서 복귀하면 지난 예약을 다시 확인합니다. PC 자동 켜기 및 Windows 서비스는 포함하지 않습니다.

## 상태 판정

- **자체 실행:** 실행 ID별 Tcl 이벤트와 새 GUI 상태 전이 또는 작업별 로그 규칙으로 시작·완료를 확인합니다. `begin simulation` 반환은 완료가 아닙니다. 완료 승인 → 저장 성공 이벤트 → 해당 PowerSI 및 알려진 자식 프로세스 종료가 모두 확인되어야 다음 작업을 진행합니다.
- **외부 실행:** PID·시작 시각·실행 경로와 자식 프로세스를 2초 간격으로 관측합니다. 기본 저부하 조건은 180초 연속, 단일 코어 환산 CPU 1% 미만, 메모리 변화 합계 16 MiB 이하, I/O 64 KiB/s 미만입니다. 이 경우 **유휴 추정**으로 실행을 허용합니다. 관측 누락·프로세스 변경·절전 복귀는 측정을 다시 시작합니다.
- 읽을 수 있는 상태 표시줄의 실행·일시정지·라이선스 대기 신호는 저부하 추정보다 우선합니다. LLM과 화면 인식은 사용하지 않습니다. PowerSI UI 접근은 별도 읽기 전용 helper에 시간 제한을 두며, 응답 없는 helper만 종료합니다.
- 모드별 로그 규칙은 설정에서 입력합니다. **작업 이름 / SPD 한 줄 식별 정규식 / 시작 정규식 / 완료 정규식**이 모두 필요합니다. 정확히 하나의 SPD 규칙만 일치해야 적용합니다. 기본은 대소문자를 구분하며 `(?i)`로 무시할 수 있습니다. 검증되지 않은 기본 완료 규칙은 배포하지 않습니다. `AFS Finished`를 모든 모드에 적용하지 마세요.

실행 거부가 명확한 경우에만 자체 PowerSI에 저장 없이 종료를 요청하고, 종료 확인 후 다음 파일로 진행합니다. 활동·관측 실패 등으로 시작 여부가 불명확하거나 저장·정리가 실패하면 자동 진행을 보류합니다. 기본 시작 확인 제한시간은 10분입니다.

## 데이터와 로그

`%LocalAppData%\SPDScheduler`에 `queue.json`, 이전 저장본 `queue.json.bak`, `logs`, `runs/<실행 ID>`를 보관합니다. 큐는 임시 파일 쓰기·디스크 flush·원자적 교체로 저장하며, 실행 ID는 프로세스 생성 전에 기록됩니다. 손상된 큐를 조용히 초기화하지 않습니다.

실행별 폴더에는 `events.log`, `output.log`, `runtime-errors.log`, `error.txt`, 생성된 `run.tcl`, 복구 및 제어 토큰이 저장됩니다. 행의 우클릭 **로그 폴더 열기**로 확인하세요. 파일 경로와 로그에 제품 정보가 포함될 수 있으므로 공개 이슈에는 원본을 올리지 마세요. 설치 제거 시 대기열·로그는 보존됩니다.

## 빌드와 테스트

필수 도구: Windows x64, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), [Inno Setup](https://jrsoftware.org/isinfo.php), Git. 별도 NuGet 테스트 프레임워크 없이 실행 가능한 콘솔 테스트를 사용합니다.

```powershell
dotnet build SPDScheduler.slnx -c Release
dotnet run --project tests/SPDScheduler.Tests -c Release --no-build
./tools/Build.ps1
```

`Build.ps1`은 테스트 후 런타임을 포함한 단일 EXE와 사용자 단위 설치파일을 만듭니다. `ISCC` 환경 변수 또는 PATH로 Inno Setup 컴파일러를 지정할 수 있습니다. 산출물은 `artifacts/installer`의 설치 EXE, SHA-256, `build-manifest.json`입니다. GitHub Actions도 같은 스크립트로 빌드하며 artifact를 제공합니다. CI에서 PowerSI를 실행하거나 정식 Release를 자동 게시하지 않습니다.

PowerSI가 차단된 UI 확인 방법:

```powershell
./artifacts/publish/SPDScheduler.exe --safe-mode --show
# 별도 임시 데이터 폴더를 지정하려면 --safe-mode와 함께 사용
./artifacts/publish/SPDScheduler.exe --safe-mode --show --data-dir "C:\Temp\SPD Scheduler UI"
```

안전 모드는 PowerSI 탐지·실행·제어 어댑터를 대체하고 별도 큐를 사용합니다. 일반 실행에서는 `--data-dir`를 허용하지 않습니다. 자동 테스트는 가짜 어댑터·프로세스 스냅샷·합성 로그로 수행하며 PowerSI나 Tcl을 실행하지 않습니다.

## 구조

| 경로 | 역할 |
|---|---|
| `src/SPDScheduler.Core` | 큐, 예약 우선순위, 상태 전이, 유휴 판정, 원자적 저장 |
| `src/SPDScheduler.Windows` | 프로세스 관측, 읽기 전용 UI helper, PowerSI Tcl 어댑터 |
| `src/SPDScheduler.App` | 트레이, 목록, 파일명 스크롤, 설정, 중복 실행 방지 |
| `tests/SPDScheduler.Tests` | 시간·실행 어댑터를 교체하는 회귀 테스트 |
| `installer`, `tools`, `.github/workflows` | 사용자 단위 설치, 해시, 검증 후 릴리스 절차 |

## 알려진 한계

저부하는 계산 완료의 증명이 아닙니다. 외부 작업이 라이선스를 기다리지만 상태가 읽히지 않으면 유휴로 오인할 수 있으므로 실제 환경에서 기준을 검증해야 합니다. 스케줄러 시작 이전에 부모 PowerSI가 사라진 외부 고아 solver는 부모 관계를 복원할 수 없습니다. 자체 실행에서 이미 기록한 자식 PID는 재시작 후에도 별도로 확인합니다.

짧은 시뮬레이션의 시작 전이를 놓쳤거나 로그 규칙이 없으면 완료를 자동 승인하지 않습니다. SPD 모드 식별은 UTF-8/BOM 텍스트의 각 줄을 읽으며, 1 MiB를 넘는 단일 줄이나 모호한 규칙은 거부합니다. 대규모 SPD의 첫 규칙 탐색에는 시간이 걸릴 수 있습니다. 로그 판정은 최근 256 KiB를 사용합니다. 지원 모드·인코딩·상태 문구는 별도 PC 검증 결과에 한정됩니다.

현재 설치파일은 코드 서명되지 않았습니다. PowerSI 및 Cadence 설치파일은 포함하지 않으며 Cadence와 독립적인 도구입니다.
