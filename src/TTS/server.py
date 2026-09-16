from __future__ import annotations

import asyncio
import json
import os
import threading
from datetime import datetime
from pathlib import Path

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field


ROOT = Path(__file__).resolve().parent
SETTINGS_PATH = ROOT / "settings.json"
SETTINGS = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
APP_ROOT = Path(
    os.environ.get(
        "VOCALINK_ROOT",
        Path(os.environ.get("LOCALAPPDATA", Path.home())) / "VocaLink",
    )
)


def resolve_setting(name: str) -> Path:
    configured = Path(SETTINGS[name])
    # 模型资源始终相对当前 TTS 服务目录解析，使源码和发布包使用同一份配置。
    return configured if configured.is_absolute() else (ROOT / configured).resolve()


GPT_MODEL = resolve_setting("gpt_model")
SOVITS_MODEL = resolve_setting("sovits_model")
REFERENCE_AUDIO = resolve_setting("reference_audio")
REFERENCE_TEXT = resolve_setting("reference_text")
MODELS_DIR = resolve_setting("models_dir")
OUTPUT_DIR = APP_ROOT / "Chat" / "Output"

app = FastAPI(title="VocaLink GPT-SoVITS Service", version="1.0.0")
engine = None
engine_lock = threading.Lock()


class SynthesisRequest(BaseModel):
    text: str = Field(min_length=1, max_length=500)
    language: str = "auto"


def required_files() -> dict[str, bool]:
    return {
        "gpt_model": GPT_MODEL.is_file(),
        "sovits_model": SOVITS_MODEL.is_file(),
        "reference_audio": REFERENCE_AUDIO.is_file(),
        "reference_text": REFERENCE_TEXT.is_file(),
    }


def load_engine():
    global engine
    if engine is not None:
        return engine

    missing = [name for name, exists in required_files().items() if not exists]
    if missing:
        raise RuntimeError(f"缺少 TTS 资源：{', '.join(missing)}")

    with engine_lock:
        if engine is not None:
            return engine

        from gsv_tts import TTS

        MODELS_DIR.mkdir(parents=True, exist_ok=True)
        loaded = TTS(
            models_dir=str(MODELS_DIR),
            device="cuda",
            is_half=True,
            compile_mode=None,
            use_flash_attn=False,
            use_bert=True,
        )
        loaded.load_gpt_model(str(GPT_MODEL))
        loaded.load_sovits_model(str(SOVITS_MODEL))
        engine = loaded
        return engine


def synthesize_to_file(text: str) -> Path:
    loaded = load_engine()
    prompt_text = REFERENCE_TEXT.read_text(encoding="utf-8").strip()
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    output_path = OUTPUT_DIR / f"{timestamp}.wav"

    with engine_lock:
        clip = loaded.infer(
            spk_audio_path=str(REFERENCE_AUDIO),
            prompt_audio_path=str(REFERENCE_AUDIO),
            prompt_audio_text=prompt_text,
            text=text.strip(),
        )
        clip.save(str(output_path))

    return output_path


@app.get("/health")
async def health():
    return {
        "status": "ok",
        "model_loaded": engine is not None,
        "output_dir": str(OUTPUT_DIR.resolve()),
        "resources": required_files(),
        "cuda_requested": True,
    }


@app.post("/load")
async def load():
    try:
        await asyncio.to_thread(load_engine)
        return {"status": "ready"}
    except Exception as exception:
        raise HTTPException(status_code=503, detail=str(exception)) from exception


@app.post("/synthesize")
async def synthesize(request: SynthesisRequest):
    try:
        output_path = await asyncio.to_thread(synthesize_to_file, request.text)
        return {"audio_path": str(output_path), "language": request.language}
    except Exception as exception:
        raise HTTPException(status_code=500, detail=str(exception)) from exception


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        app,
        host=SETTINGS["host"],
        port=int(os.environ.get("VOCALINK_TTS_PORT", SETTINGS["port"])),
        log_level="info",
    )
