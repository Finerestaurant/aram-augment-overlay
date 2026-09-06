# C# 포팅 노트

파이썬 구현을 C#(.NET 10)으로 옮기면서 확인한 것들. 요지는 **어디까지 똑같이 맞출 수
있고 어디부터는 원리적으로 불가능한가**이며, 그에 따라 검증 기준을 어디에 두었는지다.

포팅본은 `dotnet/AramOverlay.SelfTest` 로 검증한다. 대조 데이터는 파이썬 쪽에서 생성한다:

```
python scripts/gen_parity.py         # difflib 비율 2511쌍
python scripts/gen_ocr_expect.py     # fixture별 OCR 결과와 매칭 판정 84건
python scripts/gen_detect_expect.py  # fixture별 게이트·밝기·등급 14프레임
dotnet run --project dotnet/AramOverlay.SelfTest
```

## 빌드와 배포

```
dotnet build dotnet/AramOverlay.slnx
dotnet publish dotnet/AramOverlay.App -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

실측 비교 (같은 PC, 콜드 스타트):

| | 파이썬 (PyInstaller) | C# (.NET 10) |
|---|---|---|
| 단일 exe 크기 | 73.2 MB | **68.2 MB** |
| 창이 뜨기까지 | 4.4초 | **1.9초** |
| 서비스 준비까지 | 4.7초 | **2.7초** |
| 실행 의존성 | 없음 | 없음 |

크기는 기대만큼 줄지 않았다. 자체 포함 배포에 들어가는 것 중 큰 것이
`Microsoft.Windows.SDK.NET.dll` 23.7MB인데, 이건 WinRT 프로젝션 전체다 — OCR과 이미지
디코딩만 쓰는데도 SDK 전부가 따라온다.

**트리밍은 불가능하다.** WPF(NETSDK1168)와 WinForms(NETSDK1175) 둘 다 트리밍을 막는다.
WinForms는 트레이 아이콘 하나 때문에 참조하고 있었고 23MB를 차지했으므로
`Shell_NotifyIcon` P/Invoke로 직접 구현해 제거했다(`TrayIcon.cs`, 약 100줄). 그래도 WPF가
여전히 막으므로 트리밍은 못 쓴다. WinForms 제거로 78.2MB → 68.2MB.

## 의존성

NuGet 패키지와 네이티브 DLL 없이 BCL과 WinRT만 쓴다. OBS websocket은
`ClientWebSocket`, JSON은 `System.Text.Json`, 위젯 서버는 `HttpListener`, OCR은
`Windows.Media.Ocr`, 이미지 디코딩은 `Windows.Graphics.Imaging` 이다.

OpenCV는 쓰지 않는다. 이 도구가 OpenCV에서 실제로 쓰는 것은 `cvtColor`, `Sobel`,
`INTER_AREA` 리사이즈, 고정 위치에서의 `matchTemplate` 넷뿐이고, OpenCvSharp을 넣으면
자체 포함 exe에 네이티브 DLL 약 30MB가 붙는다. 넷 다 `Cv.cs` 에 직접 구현했다.

WinRT 픽셀 접근에 흔히 쓰는 `IMemoryBufferByteAccess` COM 인터페이스는 CsWinRT에서
캐스팅이 막혀 있다(`Invalid cast from 'WinRT.IInspectable'`). `IBuffer` 경유로
우회했고, 덕분에 `unsafe` 코드도 없다.

## 완전히 일치하는 것

**difflib.SequenceMatcher.ratio()** — 2511쌍에서 최대 오차 0. 부동소수점 오차조차 없다.

이게 가장 중요했다. `OCR_MIN_SCORE = 0.60` 은 "확인된 오독 0.67과 잘못된 매칭 0.50
사이의 빈 구간"이라는 측정 결과이므로, 유사도 함수가 조금이라도 다르면 임계값의 근거가
통째로 무효가 된다. 그래서 Ratcliff/Obershelp 재귀를 파이썬 구현 그대로 옮겼다. 길이 200
이상에서 발동하는 autojunk 휴리스틱은 증강 이름이 근처에도 못 가므로 구현하지 않았다.

## 원리적으로 일치시킬 수 없는 것

### cvtColor(BGR2GRAY)

무작위 색상 30만 개로 조사한 결과, OpenCV의 출력은 **어떤 닫힌 수식으로도 재현되지
않는다**:

| 구현 | 일치율 |
|---|---|
| 고정소수점 `(b·1868 + g·9617 + r·4899 + 8192) >> 14` | 99.74% |
| float64 `round(0.114b + 0.587g + 0.299r)` | 99.86% |
| shift 15 / 12 / 10 / 8 변형 | 더 나쁨 |

채널을 하나씩 격리해 테스트하면 거의 다 맞는데 조합했을 때만 어긋난다. IPP를 꺼도
동일하다. SIMD 경로가 픽셀 위치에 따라 다른 코드 경로를 타기 때문으로 보이며, 이는
**OpenCV의 출력 자체가 빌드와 CPU에 따라 달라질 수 있다**는 뜻이기도 하다. 즉 맞출
대상이 애초에 고정된 값이 아니다.

가장 근접한 float64 형태를 채택했다. 남는 차이는 픽셀의 0.14%에서 ±1이다.

### JPEG 디코딩

WinRT와 OpenCV의 JPEG 디코더는 IDCT 구현이 달라 ±1 수준으로 어긋난다. PNG는 정확히
일치한다.

### 상류 차이가 하류에 미치는 영향 (실측)

| 값 | 최대 오차 | 관련 임계값 | 여유 |
|---|---|---|---|
| 영역 밝기 평균 | 0.0018 | `HOVER_SPREAD` 10.0 | 5,000배 |
| 게이트·숨김버튼 점수 | 0.00106 | `GATE_OPEN` 0.45 (양성 최저 0.659, 음성 최고 0.352) | 100배 이상 |

## 검증 기준

바이트 단위 일치가 불가능한 지점이 있으므로, 테스트는 **판정**을 본다. 픽셀 값이 아니라
그 값으로 내리는 결정이 같아야 한다:

- 게이트 열림/닫힘, 숨김 버튼 존재 여부, 호버 카드, 등급(silver/gold/prismatic) — 완전 일치 요구
- OCR — 읽은 문자열이 아니라 **매칭된 증강과 임계값 통과 여부**가 일치할 것
- 밝기와 점수 — 위 표의 실측 오차보다 넉넉하되 임계값 여유보다는 훨씬 작은 허용치

OCR에서 84건 중 83건은 문자열까지 동일하다. 유일한 예외는 3배 확대한 툴팁으로, 양쪽 다
판독 불능이며 한 글자만 다르다(`사그 入 八2혜 曰...` / `사그 入 八2다 曰...`). 두 문자열
모두 `상급 조준경 부착` 을 동일한 점수 0.2400으로 뽑고 똑같이 기각하므로 판정은 같다.
이는 이미 알려진 케이스다 — 이 툴팁은 1배에서 정확히 읽히고 3배에서 무너진다.
