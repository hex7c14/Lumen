"""Vendored minimal inference-only 超分网络结构（不依赖 basicsr）。

来源与许可：
- RRDBNet / ResidualDenseBlock / RRDB：来自 BasicSR (https://github.com/XPixelGroup/BasicSR,
  Apache-2.0) 与 Real-ESRGAN (https://github.com/xinntao/Real-ESRGAN, BSD-3-Clause) 的
  basicsr/archs/rrdbnet_arch.py，此处仅保留前向推理所需代码。
- SRVGGNetCompact：来自 Real-ESRGAN 的 realesrgan/archs/srvgg_arch.py
  (https://github.com/xinntao/Real-ESRGAN, BSD-3-Clause)。
仅用于把官方 .pth 权重导出为 ONNX；不做任何训练相关逻辑。
"""
import torch
from torch import nn
from torch.nn import functional as F


# ---------------- RRDBNet (Real-ESRGAN x4plus / anime-6B / 4x-UltraSharp) ----------------
class ResidualDenseBlock(nn.Module):
    def __init__(self, num_feat=64, num_grow_ch=32):
        super().__init__()
        self.conv1 = nn.Conv2d(num_feat, num_grow_ch, 3, 1, 1)
        self.conv2 = nn.Conv2d(num_feat + num_grow_ch, num_grow_ch, 3, 1, 1)
        self.conv3 = nn.Conv2d(num_feat + 2 * num_grow_ch, num_grow_ch, 3, 1, 1)
        self.conv4 = nn.Conv2d(num_feat + 3 * num_grow_ch, num_grow_ch, 3, 1, 1)
        self.conv5 = nn.Conv2d(num_feat + 4 * num_grow_ch, num_feat, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(negative_slope=0.2, inplace=True)

    def forward(self, x):
        x1 = self.lrelu(self.conv1(x))
        x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
        x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
        x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
        x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
        return x5 * 0.2 + x


class RRDB(nn.Module):
    def __init__(self, num_feat, num_grow_ch=32):
        super().__init__()
        self.rdb1 = ResidualDenseBlock(num_feat, num_grow_ch)
        self.rdb2 = ResidualDenseBlock(num_feat, num_grow_ch)
        self.rdb3 = ResidualDenseBlock(num_feat, num_grow_ch)

    def forward(self, x):
        out = self.rdb1(x)
        out = self.rdb2(out)
        out = self.rdb3(out)
        return out * 0.2 + x


class RRDBNet(nn.Module):
    """Real-ESRGAN 主网络。scale=4 时 num_in_ch/num_out_ch 为 3。"""

    def __init__(self, num_in_ch=3, num_out_ch=3, scale=4, num_feat=64, num_block=23, num_grow_ch=32):
        super().__init__()
        assert scale == 4, "本管线只导出 4x 模型"
        self.scale = scale
        self.conv_first = nn.Conv2d(num_in_ch, num_feat, 3, 1, 1)
        self.body = nn.Sequential(*[RRDB(num_feat, num_grow_ch) for _ in range(num_block)])
        self.conv_body = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_up1 = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_up2 = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_hr = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_last = nn.Conv2d(num_feat, num_out_ch, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(negative_slope=0.2, inplace=True)

    def forward(self, x):
        fea = self.conv_first(x)
        body_feat = self.conv_body(self.body(fea))
        fea = fea + body_feat
        fea = self.lrelu(self.conv_up1(F.interpolate(fea, scale_factor=2, mode='nearest')))
        fea = self.lrelu(self.conv_up2(F.interpolate(fea, scale_factor=2, mode='nearest')))
        out = self.conv_last(self.lrelu(self.conv_hr(fea)))
        return out


# ---------------- Old-arch RRDBNet (4x-UltraSharp 等社区 ESRGAN 模型) ----------------
# 4x-UltraSharp.pth 的 state_dict key 形如 model.1.sub.0.RDB1.conv1.0.weight，
# 对应旧版 ESRGAN / BasicSR 的 RRDBNet_arch.py 结构：convN 是只含一个 Conv2d 的
# Sequential，整网用 nn.Sequential 组织（索引 2/4/7/9 为 LeakyReLU，无参数）。
class _OldRDB(nn.Module):
    def __init__(self, nf=64, gc=32):
        super().__init__()
        self.conv1 = nn.Sequential(nn.Conv2d(nf, gc, 3, 1, 1))
        self.conv2 = nn.Sequential(nn.Conv2d(nf + gc, gc, 3, 1, 1))
        self.conv3 = nn.Sequential(nn.Conv2d(nf + 2 * gc, gc, 3, 1, 1))
        self.conv4 = nn.Sequential(nn.Conv2d(nf + 3 * gc, gc, 3, 1, 1))
        self.conv5 = nn.Sequential(nn.Conv2d(nf + 4 * gc, nf, 3, 1, 1))
        self.lrelu = nn.LeakyReLU(negative_slope=0.2, inplace=True)

    def forward(self, x):
        x1 = self.lrelu(self.conv1(x))
        x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
        x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
        x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
        x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
        return x5 * 0.2 + x


class _OldRRDB(nn.Module):
    def __init__(self, nf=64, gc=32):
        super().__init__()
        self.RDB1 = _OldRDB(nf, gc)
        self.RDB2 = _OldRDB(nf, gc)
        self.RDB3 = _OldRDB(nf, gc)

    def forward(self, x):
        out = self.RDB1(x)
        out = self.RDB2(out)
        out = self.RDB3(out)
        return out * 0.2 + x


class _OldTrunk(nn.Module):
    """model.1：sub = [RRDB x nb, trunk_conv]。"""

    def __init__(self, nf=64, nb=23, gc=32):
        super().__init__()
        self.sub = nn.Sequential(*[_OldRRDB(nf, gc) for _ in range(nb)],
                                 nn.Conv2d(nf, nf, 3, 1, 1))

    def forward(self, x):
        return self.sub(x)


class OldRRDBNet(nn.Module):
    def __init__(self, nf=64, nb=23, gc=32):
        super().__init__()
        self.model = nn.Sequential(
            nn.Conv2d(3, nf, 3, 1, 1),      # 0 conv_first
            _OldTrunk(nf, nb, gc),          # 1 RRDB trunk + trunk_conv
            nn.LeakyReLU(0.2, True),        # 2
            nn.Conv2d(nf, nf, 3, 1, 1),     # 3 upconv1
            nn.LeakyReLU(0.2, True),        # 4
            nn.Identity(),                  # 5 (interpolate 占位)
            nn.Conv2d(nf, nf, 3, 1, 1),     # 6 upconv2
            nn.LeakyReLU(0.2, True),        # 7
            nn.Conv2d(nf, nf, 3, 1, 1),     # 8 HRconv
            nn.LeakyReLU(0.2, True),        # 9
            nn.Conv2d(nf, 3, 3, 1, 1),      # 10 conv_last
        )

    def forward(self, x):
        fea = self.model[0](x)
        trunk = self.model[1](fea)
        fea = fea + trunk
        fea = self.model[4](self.model[3](F.interpolate(fea, scale_factor=2, mode='nearest')))
        fea = self.model[7](self.model[6](F.interpolate(fea, scale_factor=2, mode='nearest')))
        out = self.model[10](self.model[9](self.model[8](fea)))
        return out


# ---------------- SRVGGNetCompact (realesr-general-x4v3) ----------------
def _activation(act_type, num_feat):
    if act_type == 'relu':
        return nn.ReLU(inplace=True)
    if act_type == 'lrelu':
        return nn.LeakyReLU(0.1, inplace=True)
    if act_type == 'prelu':
        return nn.PReLU(num_parameters=num_feat)
    if act_type == 'silu':
        return nn.SiLU(inplace=True)
    raise ValueError(f'Unknown act_type {act_type}')


class SRVGGNetCompact(nn.Module):
    def __init__(self, num_in_ch=3, num_out_ch=3, num_feat=64, num_conv=32, upscale=4, act_type='prelu'):
        super().__init__()
        self.upscale = upscale
        self.body = nn.ModuleList()
        self.body.append(nn.Conv2d(num_in_ch, num_feat, 3, 1, 1))
        self.body.append(_activation(act_type, num_feat))
        for _ in range(num_conv):
            self.body.append(nn.Conv2d(num_feat, num_feat, 3, 1, 1))
            self.body.append(_activation(act_type, num_feat))
        self.body.append(nn.Conv2d(num_feat, num_out_ch * upscale * upscale, 3, 1, 1))
        self.upsampler = nn.PixelShuffle(upscale)

    def forward(self, x):
        out_feature = x
        for layer in self.body:
            out_feature = layer(out_feature)
        out = self.upsampler(out_feature)
        # 官方实现的关键一步：加上最近邻上采样的输入作为全局残差（x4v3 只学残差）
        base = F.interpolate(x, scale_factor=self.upscale, mode='nearest')
        return out + base
