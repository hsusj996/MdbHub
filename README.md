# MdbHub

Access `.mdb`로 관리하는 사양 데이터를 안전하게 다루는 WPF 도구입니다. 수작업으로 인한 휴먼에러를 줄이는 것이 목적입니다.

| 탭 | 하는 일 |
|---|---|
| 조회 | mdb의 테이블을 열어 보고, 컬럼 필터·검색으로 걸러 봅니다. |
| 추출 | 컬럼 값(체크 방식 필터)으로 걸러낸 행만 모아 새 mdb를 만듭니다. |
| 반영 | 추출한(또는 삽입할) 행이 담긴 mdb를 다른 mdb에 덮어쓰기/삽입합니다. 미리보기 → 확인 → 자동 백업 → 트랜잭션 순서로 진행하고, 실패하면 전부 되돌립니다. |
| 비교 | 두 mdb의 같은 테이블을 키 기준으로 좌우로 나란히 보여주고(머지 툴처럼) 차이 줄·셀에 색을 칠합니다. 스크롤이 양쪽 같이 움직이고, "이전/다음 차이"로 이동합니다. 테이블 구성이 달라도 공통 테이블이 하나라도 있으면 열리고, 한쪽에만 있는 테이블이나 구조가 다른 테이블은 목록에 비고로 표시합니다. |
| 설정 | 테이블별 키 컬럼과 추출 필터 정의를 편집합니다. |

자세한 요구사항과 결정 사항은 [docs/requirements.md](docs/requirements.md)를 보세요.

## 필요한 것

- Windows, .NET 10 SDK
- **64비트 Microsoft Access Database Engine (ACE)** — [Access Database Engine 2016 Redistributable](https://www.microsoft.com/download/details.aspx?id=54920)의 `AccessDatabaseEngine_X64.exe`. 64비트 Office가 이미 있으면 보통 설치돼 있습니다.
- **Telerik UI for WPF 2025.3.x** 라이선스. 라이선스가 2025 Q3까지라 `Telerik.UI.for.Wpf.80.Xaml` **2025.3.813**을 씁니다. 이보다 높은 버전으로 올리지 마세요.
  1. Telerik CLI를 설치하고 `telerik login`
  2. `telerik nuget config` (Telerik NuGet 피드가 사용자 NuGet 설정에 등록됩니다. 인증 정보는 저장소에 들어가지 않습니다.)
  3. `telerik license get-key`

## 빌드와 테스트

```powershell
dotnet build MdbHub.sln
dotnet test tests/MdbHub.Tests      # ACE가 설치돼 있어야 합니다 (실제 mdb 파일을 만들어 검증)
dotnet run --project src/MdbHub.App
```

## 샘플 mdb

`samples/`에 작은 샘플(`sample_a.mdb`, `sample_b.mdb`)이 있습니다. 비교 탭에서 두 파일을 열어 보세요.
(공통 약액코드는 값이 다르고, 300/x는 A에만, 400/1은 B에만 있습니다.)

최대 규모 샘플(약액코드 70개 × 2,000행 = 테이블당 14만 행, 약 149MB)은 GitHub 파일 크기 제한 때문에 저장소에 없습니다. 아래로 다시 만들 수 있습니다 (몇 분 걸립니다).

```powershell
dotnet run --project tools/MdbHub.SampleGen -- samples max     # small | max | all
```

## 설정 파일

`mdbhub.config.json`이 앱 실행 파일 옆에 만들어집니다 (저장소에는 올리지 않습니다). 형식은 [mdbhub.config.example.json](mdbhub.config.example.json)을 참고하세요.

- **테이블 키**: 반영·비교에서 "같은 행"을 판단하는 컬럼. 설정에 없으면 mdb의 기본 키를 쓰고, 그것도 없으면 처음 쓸 때 물어본 뒤 저장합니다.
- **필터**: 한 필터의 컬럼들은 같은 의미의 별칭입니다. 테이블마다 그중 있는 컬럼을 씁니다 (예: `CHEMICAL_NO` / `ID`).
- 저장할 때 이전 파일은 `.bak`으로 남습니다.

## 구조

```
src/MdbHub.Core   설정, 스키마 검증, 키 결정, 행 비교 (DB 의존 없음)
src/MdbHub.Data   OleDb(ACE)로 mdb 읽기/추출/반영/백업, 샘플 생성
src/MdbHub.App    WPF 화면 (MVVM)
tests/            xUnit 테스트
tools/            샘플 mdb 생성기
```
