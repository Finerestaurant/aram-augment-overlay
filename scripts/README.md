# 대조 데이터 생성 스크립트

`tests/` 의 대조 데이터를 만든 스크립트들입니다. **지금 상태로는 실행되지 않습니다** —
파이썬 구현(`aram_overlay/`)을 import 하는데, 그 코드는 C# 이식이 끝나면서 트리에서 빠졌습니다.

다시 만들어야 한다면 파이썬 구현이 마지막으로 존재하던 커밋에서 꺼내면 됩니다:

```
git checkout ecd9cb5 -- aram_overlay requirements.txt
python -m venv .venv && .venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python scripts\gen_parity.py         # difflib 비율 2511쌍
.venv\Scripts\python scripts\gen_ocr_expect.py     # fixture별 OCR 결과와 매칭 판정
.venv\Scripts\python scripts\gen_detect_expect.py  # fixture별 게이트·밝기·등급
```

보관하는 이유는, C# 구현이 무엇을 기준으로 맞춰졌는지가 이 스크립트에 적혀 있기 때문입니다.
어떤 값을 어떤 허용치로 비교하는지, 왜 문자열이 아니라 판정을 비교하는지는
[`docs/PORTING.md`](../docs/PORTING.md)에 정리돼 있습니다.
