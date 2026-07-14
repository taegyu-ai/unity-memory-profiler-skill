# Memory Profiler Skill (Preview)

**Language:** [English](../README.md) | 한국어

Unity AI Assistant로 Memory Profiler 스냅샷을 분석하는 프리뷰 스킬입니다. 정식 내장 스킬이 나오기 전에, 지금 바로 써볼 수 있게 만들었습니다.

프로젝트 메모리 사용량을 물어보면 **Wide Survey**를 돌립니다. Unity 오브젝트 타입, 네이티브 서브시스템, 매니지드 힙 전반에서 개선 후보를 뽑아 **resident** 메모리 기준으로 순위를 매기고요. 그중 골라서 물어보면 크기·중복·retention 경로까지 파고들어 줄일 방법을 제안합니다.

## 이 패키지 구성

| 폴더 | 내용 |
|---|---|
| `Editor/` | 두 스킬이 함께 쓰는 툴 어셈블리. Unity AI Assistant `[AgentTool]`(`Unity.MemoryProfiler.*`)로 노출되는 분석 툴 13개(단일 스냅샷 서베이, 2-스냅샷 비교, Unrooted 콜스택 분해)와 커스터마이즈 툴 2개가 들어있습니다. Memory Profiler 패키지 자체의 모델 빌더를 그대로 감싸는 방식이라 측정값은 바이트 단위까지 정확합니다. |
| `AIAssistantSkills/unity-memory-profiling-skill/` | **분석** 스킬(`SKILL.md` + `references/`). 2단계 서베이, 2-스냅샷 비교 모드, Unrooted 할당 드릴을 이끕니다. |
| `AIAssistantSkills/unity-memory-customization-skill/` | **커스터마이즈** 스킬(실험적). 프로젝트가 받아들인 비용·의도된 특성(project 레이어)과 어디서나 통하는 노하우(playbook 레이어), 이렇게 두 오버레이를 큐레이션합니다. `RecordCustomization(layer=…)`로 기록해두면 분석 스킬이 `baseline → playbook → project-customization` 순서로 불러오고, 일치하는 항목엔 `✅ accepted (project)` 표시를 남깁니다. 그래야 health check 때 정말 조치가 필요한 것만 눈에 띕니다. |
| `Samples~/DefaultOverlays/` | 두 오버레이 파일의 시작 템플릿(아래 "커스터마이즈 데이터는 패키지가 아니라 프로젝트에 남습니다" 참고). 설치된 패키지 내용엔 포함되지 않고, 원할 때 따로 import해야 합니다. |

## 요구사항

- Unity **6000.3.13+**
- `com.unity.memoryprofiler` **>= 1.1.0**
- `com.unity.ai.assistant` (Unity AI Assistant)
- Memory Profiler 패키지는 반드시 **레지스트리에서** 받아야 합니다(로컬 소스로 embed하면 안 됩니다). 이 패키지의 Editor 어셈블리는 asmdef 이름을 `Unity.MemoryProfiler.Editor.Tests`로 지어서 Memory Profiler 내부에 접근하는데, 이미 그 패키지에 `[assembly: InternalsVisibleTo("Unity.MemoryProfiler.Editor.Tests")]`가 박혀 있어서 따로 손댈 필요는 없습니다. 다만 `com.unity.memoryprofiler`가 embed 상태라면 그 패키지 자체의 테스트 어셈블리가 이미 그 이름을 쓰고 있어서 충돌이 납니다 — 레지스트리 설치라면 이름이 비어 있으니 문제없습니다. (이건 `com.unity.memoryprofiler` 쪽 제약이지 이 스킬 패키지 얘기가 아닙니다. *이* 패키지를 git URL로 설치하는 건 정상적인, 지원되는 방법입니다.)

## 설치

**Package Manager (권장)**: Window ▸ Package Manager ▸ `+` ▸ *Add package from git URL* 을 누르고 아래 주소를 붙여넣으세요.

```
https://github.com/taegyu-ai/unity-memory-profiler-skill.git
```

`Packages/manifest.json`에 직접 추가해도 됩니다:
```json
"com.taegyu-ai.unity-memory-profiler-skill": "https://github.com/taegyu-ai/unity-memory-profiler-skill.git"
```

**로컬/embedded 소스로 쓰기(스킬 자체를 개발·디버깅할 때)**: 이 repo를 클론해서 `manifest.json`에 `file:` 의존성으로 추가하거나, 프로젝트의 `Packages/` 밑에 폴더째로 넣으세요.

설치 후:
1. Unity가 컴파일을 마치면 **Project Settings ▸ AI ▸ Skills**를 열어 **Rescan**하고, 두 스킬이 활성 상태인지 확인합니다(이번엔 `Assets/`가 아니라 **Package**로 설치된 걸로 표시됩니다).
2. **Memory Profiler** 창을 열어 `.snap` 캡처를 로드합니다.
3. **새** Assistant 대화를 시작해서 스냅샷 메모리를 분석해달라고 물어봅니다.

> **왜 스킬을 바꿀 때마다 새 대화를 열어야 할까요?** Assistant는 스킬 *정의*(`SKILL.md`, 툴 목록)를 대화의 *첫 프롬프트*에서만 전달받습니다. 이 패키지를 설치하거나 업데이트했다면, 새 대화를 열어야 바뀐 정의가 반영됩니다. 활성화 여부는 대화의 **Thoughts**(`Activate Skill: unity-memory-profiling-skill`)에서 확인할 수 있습니다.

### 커스터마이즈 데이터는 패키지가 아니라 프로젝트에 남습니다

커스터마이즈 스킬은 프로젝트별 메모(수용한 비용, 조정된 임계값)를 파일 두 개에 기록합니다. 이 파일들은 패키지가 업데이트되거나 삭제돼도 그대로 남아있어야 해서, 애초에 패키지 안에 설치되는 방식이 아닙니다. 대신 프로젝트의 `Assets/` 밑에 자리 잡고, 다른 프로젝트 에셋과 똑같이 프로젝트가 소유하고 버전관리합니다.

- **패키지만 설치하면 baseline 분석은 바로 됩니다.** 별다른 준비가 필요 없습니다.
- 빈 상태 말고 편집 가능한 기본값(일반적인 권고, 기본 임계값)으로 시작하고 싶다면, **Package Manager ▸ 이 패키지 ▸ Samples**에서 "Default Overlays" 옆 **Import**를 누르세요. `playbook.md`/`project-customization.md` 시작 템플릿이 `Assets/Samples/.../DefaultOverlays/`로 복사되고, 커스터마이즈·분석 스킬이 거기서 찾아 씁니다.
- Import를 건너뛰어도 상관없습니다. Sample을 가져오거나 커스터마이즈를 한 번 기록하기 전까지는(기록하는 순간 파일이 저절로 만들어지니) baseline만 돌아갈 뿐입니다.

## 문제 해결

- **Assistant가 메모리 얘기는 하는데 이 툴들을 안 부른다** (Unity 내장 프로파일러 스킬로 빠지는 경우): 스킬이 활성화되지 않은 겁니다. **Project Settings ▸ AI ▸ Skills**에서 **Rescan**하고 두 스킬 다 *Allow* 상태인지 확인한 다음, **새** 대화로 다시 시도하세요. 툴 자체가 등록됐는지만 따로 보고 싶다면 이렇게 직접 물어보세요: *"Use the Unity.MemoryProfiler tools to initialize and give a memory overview."*
- **`SKILL.md`가 아예 안 읽힌다**: frontmatter부터 확인하세요. `required_editor_version`은 순수 `MAJOR.MINOR.PATCH` 형식이어야 합니다 — `6000.4.0b7`처럼 베타/알파 접미사가 붙으면 버전 제약으로 인정받지 못하고 스킬 전체가 조용히 실패합니다. `required_packages`도 리스트가 아니라 **맵**이어야 합니다(`com.unity.memoryprofiler: ">=1.1.0"`).
- **`[AgentTool]`/`[ToolParameter]`에서 `CS0246` 컴파일 에러**: asmdef가 이 어트리뷰트들을 제공하는 어셈블리, 즉 `Unity.AI.Assistant.Runtime`(네임스페이스 `Unity.AI.Assistant.FunctionCalling`)을 참조해야 합니다. 쓰고 계신 `com.unity.ai.assistant` 버전이 이 어트리뷰트를 다른 어셈블리에서 내보낸다면, Package Manager에서 찾아 참조를 바꿔주세요.
- **이 패키지의 Editor 어셈블리에서 `internal` 접근 에러**: asmdef 이름이 정확히 `Unity.MemoryProfiler.Editor.Tests`인지, `com.unity.memoryprofiler`가 embed 상태는 아닌지 확인하세요(요구사항 참고).
- **커스터마이즈를 기록했는데 분석에 반영이 안 된다**: 분석 스킬은 `GetCustomization` 툴로 오버레이를 그때그때 직접 읽습니다(캐시된 스킬 reference가 아니라요). 그래서 새로 기록한 항목은 **바로 다음** 분석 호출부터 반영됩니다 — 오버레이 *내용*만 바뀐 거라면 Rescan이나 새 대화가 따로 필요 없습니다(패키지 *자체*를 설치·업데이트했을 땐 여전히 Rescan + 새 대화가 필요합니다). 그래도 반영이 안 되면 기록한 항목의 `scope`가 스냅샷과(platform/type/captureOrigin) 실제로 맞는지 확인해보세요. project 레이어 항목이라면 `project=*`로 두면 됩니다. 파일 자체가 이미 프로젝트 경계니까요.

## 사용법

- **분석하기**: 캡처를 로드하고 *"survey this snapshot's memory"* / *"what's using the most memory?"* 같은 식으로 물어보세요. 분석 스킬이 켜지고 서베이를 진행합니다.
- **스냅샷 두 개 비교하기**: Memory Profiler 창에서 Compare pair를 로드하거나(`.snap` 경로 두 개를 줘도 됩니다) *"compare these two snapshots — what grew?"* / *"find the leak between these captures"* 처럼 물어보세요. 타입·서브시스템·총량을 B − A로 diff해서 늘어난·새로 생긴 그룹을 짚어주고, 바뀐 것들 위주로 파고듭니다.
- **Unrooted(네이티브) 할당**: `-enable-memoryprofiler-callstacks` 플레이어 인자를 켜고 캡처했다면(Unity 6000.3+), 큰 `Native > Unrooted` 그룹에 대해 물었을 때 할당 콜스택별로 나눠서 user-code / engine-managed / native로 분류해줍니다. 콜스택이 없는 캡처라면 Unrooted 총량만 보고하고, 어떻게 다시 캡처하면 될지 알려줍니다.
- **커스터마이즈하기** (실험적): 분석이 끝난 다음, 의도된 부분을 커스터마이즈 스킬에 알려주세요. 예를 들면 *"이 4K 텍스처들은 아트 스타일 때문에 필요한 거니까 지적하지 마"* 같은 식으로요. 스킬이 이걸 일반화하고 범위(scope)를 잡아 확인받은 다음 기록해두면, 이후 분석에서 일치하는 항목엔 `✅ accepted (project)` 표시가 붙고 후보 목록에서 빠집니다. 측정값 자체는 항상 그대로 보여주고, 판단만 눌러두는 방식입니다.

## 범위와 현재 상태

- **플레이어 캡처만 지원합니다.** 에디터 캡처는 메모리가 왜곡돼서(Native/Subsystem 분석을 믿을 수 없어서) 스킬이 알아서 감지하고 거절합니다.
- **지표**: **resident**(물리) 메모리를 우선합니다. resident 값이 없는 캡처(오래된 캡처, 일부 Android)라면 committed로 대신하고, 그 사실을 캡션에 명시합니다.
- **프리뷰 상태입니다.** 분석 스킬은 실제 캡처와 독립 오라클로 end-to-end 검증을 마쳤습니다. **커스터마이즈 스킬은 아직 실험적**이고요 — 전체 루프(기록 → 저장 → 다시 로드 → scope 매칭 → 새 분석에서 `✅ accepted` 제외)는 Assistant 런타임에서 end-to-end로 확인했지만, 큐레이션 UX는 아직 다듬는 중입니다.
- 이건 **비공식 프리뷰**입니다. 앞으로 나올 수도 있는 공식 Memory Profiler 스킬과는 무관합니다.
