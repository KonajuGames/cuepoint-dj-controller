"""
Exports Demucs's htdemucs model to ONNX for Cuepoint's stem separation (STEMS mode on the decks).

ONNX has no inverse STFT, so the model is exported without its spectrogram at either end: it takes a 7.8-second
segment of stereo audio at 44.1 kHz and that segment's spectrogram (complex values as channels), and gives back each
stem's spectrogram and time branch. Cuepoint does the STFT and inverse STFT itself (Audio/Stems/Demucs.cs).

    python -m venv env
    env\\Scripts\\python -m pip install torch==2.4.1 --index-url https://download.pytorch.org/whl/cpu
    env\\Scripts\\python -m pip install demucs==4.0.1 onnx onnxruntime
    env\\Scripts\\python tools\\export_demucs.py

It writes %LOCALAPPDATA%\\Cuepoint\\models\\htdemucs.onnx, where Cuepoint looks, unless given another path. Demucs
downloads the weights (MIT licence) from Meta the first time.
"""

import math
import os
import sys

import numpy as np
import torch
from demucs.htdemucs import HTDemucs
from demucs.pretrained import get_model
from einops import rearrange


def _std(x, mean, dims):
    # As torch's std (with n - 1), but with the count a constant, so the graph doesn't read the input's shape.
    n = math.prod(x.shape[d] for d in dims)
    return (((x - mean) ** 2).sum(dim=dims, keepdim=True) / (n - 1)).sqrt()


class Core(torch.nn.Module):
    """HTDemucs.forward from just after its STFT to just before its inverse STFT."""

    def __init__(self, model: HTDemucs):
        super().__init__()
        self.m = model

    def forward(self, mix, mag):
        m = self.m
        x = mag
        B, C, Fq, T = x.shape
        mean = x.mean(dim=(1, 2, 3), keepdim=True)
        std = _std(x, mean, (1, 2, 3))
        x = (x - mean) / (1e-5 + std)

        xt = mix
        meant = xt.mean(dim=(1, 2), keepdim=True)
        stdt = _std(xt, meant, (1, 2))
        xt = (xt - meant) / (1e-5 + stdt)

        saved, saved_t, lengths, lengths_t = [], [], [], []
        for idx, encode in enumerate(m.encoder):
            lengths.append(x.shape[-1])
            inject = None
            if idx < len(m.tencoder):
                lengths_t.append(xt.shape[-1])
                tenc = m.tencoder[idx]
                xt = tenc(xt)
                if not tenc.empty:
                    saved_t.append(xt)
                else:
                    inject = xt
            x = encode(x, inject)
            if idx == 0 and m.freq_emb is not None:
                frs = torch.arange(x.shape[-2], device=x.device)
                emb = m.freq_emb(frs).t()[None, :, :, None].expand_as(x)
                x = x + m.freq_emb_scale * emb
            saved.append(x)

        if m.crosstransformer:
            if m.bottom_channels:
                b, c, f, t = x.shape
                x = rearrange(x, "b c f t-> b c (f t)")
                x = m.channel_upsampler(x)
                x = rearrange(x, "b c (f t)-> b c f t", f=f)
                xt = m.channel_upsampler_t(xt)
            x, xt = m.crosstransformer(x, xt)
            if m.bottom_channels:
                x = rearrange(x, "b c f t-> b c (f t)")
                x = m.channel_downsampler(x)
                x = rearrange(x, "b c (f t)-> b c f t", f=f)
                xt = m.channel_downsampler_t(xt)

        for idx, decode in enumerate(m.decoder):
            skip = saved.pop(-1)
            x, pre = decode(x, skip, lengths.pop(-1))
            offset = m.depth - len(m.tdecoder)
            if idx >= offset:
                tdec = m.tdecoder[idx - offset]
                length_t = lengths_t.pop(-1)
                if tdec.empty:
                    pre = pre[:, :, 0]
                    xt, _ = tdec(pre, None, length_t)
                else:
                    skip = saved_t.pop(-1)
                    xt, _ = tdec(xt, skip, length_t)

        S = len(m.sources)
        x = x.view(B, S, -1, Fq, T)
        x = x * std[:, None] + mean[:, None]
        xt = xt.view(B, S, -1, mix.shape[-1])
        xt = xt * stdt[:, None] + meant[:, None]
        return x, xt


def main():
    default = os.path.join(os.environ.get("LOCALAPPDATA", "."), "Cuepoint", "models", "htdemucs.onnx")
    out = sys.argv[1] if len(sys.argv) > 1 else default
    torch.set_num_threads(max(1, (os.cpu_count() or 2) // 2))

    model: HTDemucs = get_model("htdemucs").models[0]
    model.eval()
    assert model.cac and model.use_train_segment
    assert list(model.sources) == ["drums", "bass", "other", "vocals"], model.sources

    # The attention's fast path is one fused operator ONNX doesn't have.
    torch.backends.mha.set_fastpath_enabled(False)

    length = int(model.segment * model.samplerate)
    mix = torch.randn(1, 2, length) * 0.1
    with torch.no_grad():
        z = model._spec(mix)
        mag = model._magnitude(z)
        core = Core(model).eval()

        # The core with Demucs's own STFT either side must be the whole model.
        x, xt = core(mix, mag)
        whole = xt + model._ispec(model._mask(z, x), length)
        print("core against the whole model, largest difference:", (whole - model(mix)).abs().max().item())

        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        torch.onnx.export(
            core,
            (mix, mag),
            out,
            input_names=["mix", "spec"],
            output_names=["spec_out", "time_out"],
            opset_version=17,
            do_constant_folding=True,
        )

    import onnxruntime as ort

    options = ort.SessionOptions()
    options.intra_op_num_threads = max(1, (os.cpu_count() or 2) // 2)
    session = ort.InferenceSession(out, options, providers=["CPUExecutionProvider"])
    ox, oxt = session.run(None, {"mix": mix.numpy(), "spec": mag.numpy()})
    print("ONNX against PyTorch, largest difference:", np.abs(ox - x.numpy()).max(), np.abs(oxt - xt.numpy()).max())
    print("wrote", out, os.path.getsize(out) // (1 << 20), "MB")


if __name__ == "__main__":
    main()
