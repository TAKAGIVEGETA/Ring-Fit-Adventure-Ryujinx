# Ring Fit Adventure on Ryujinx for macOS

**让 Mac 也能运行《健身环大冒险》（Ring Fit Adventure）**

[中文](#中文) | [English](#english)

---

## 中文

### 项目简介

本项目的目的是让 **macOS 用户也能够通过 Ryujinx 游玩《健身环大冒险》（Ring Fit Adventure）**。

在 Windows 平台上，目前可以使用 **Eden Emulator** 运行《健身环大冒险》。

但是在 macOS 平台上，Eden 目前仍处于早期开发阶段，几乎无法正常运行大多数游戏。

相比之下，**Ryujinx 在 macOS 上已经可以正常运行大量 Nintendo Switch 游戏**，但原版 Ryujinx 并没有针对《健身环大冒险》所需的 **Ring-Con** 输入方式提供支持。

因此，本项目将 **Eden 中用于《健身环大冒险》的 Ring-Con 输入逻辑移植到了 Ryujinx**，使 Ryujinx 在 macOS 上也能够使用 Joy-Con + Ring-Con 游玩《健身环大冒险》。

### 使用方法

启动 Ryujinx 后，在游戏的输入设置中：

1. 勾选 **Enable Ring-Con（启用 Ring-Con）**
2. 设置两个玩家

#### 玩家 1

| 设置项                   | 设置                |
| --------------------- | ----------------- |
| Input Device（输入设备）    | **Right Joy-Con** |
| Controller Type（手柄类型） | **Right Joy-Con** |

#### 玩家 2

| 设置项                   | 设置               |
| --------------------- | ---------------- |
| Input Device（输入设备）    | **Left Joy-Con** |
| Controller Type（手柄类型） | **Dual Joy-Con** |

配置完成后即可使用：

* Ring-Con
* 右 Joy-Con
* 左 Joy-Con

正常游玩《健身环大冒险》。

### 当前状态

* ✅ macOS 上可以运行《健身环大冒险》
* ✅ 支持 Ring-Con 输入
* ✅ 支持左右 Joy-Con
* ⚠️ **目前仅在 macOS 上进行测试**
* ⚠️ Windows / Linux 尚未进行完整测试

### 项目来源

本项目基于 Ryujinx，并参考 / 移植了 Eden Emulator 中与《健身环大冒险》相关的 Ring-Con 输入逻辑。

项目的核心目标是解决：

> **Ryujinx 在 macOS 上游戏运行能力较强，但缺少 Ring-Con 支持；Eden 已经实现了相关逻辑，但 macOS 版本目前还无法很好地运行游戏。**

因此，本项目尝试将两者的优势结合起来：

**Eden 的 Ring-Con 支持 + Ryujinx 的 macOS 游戏运行能力**

从而让 Mac 用户能够游玩《健身环大冒险》。

---

## English

### About

The purpose of this project is to make **Ring Fit Adventure playable on macOS through Ryujinx**.

On Windows, **Eden Emulator** can be used to run Ring Fit Adventure.

However, Eden is still in its early stages on macOS and is currently unable to run most games properly.

Meanwhile, **Ryujinx runs a large number of Nintendo Switch games well on macOS**, but the original Ryujinx does not provide the required **Ring-Con input support** for Ring Fit Adventure.

Therefore, this project ports the **Ring-Con input logic used by Eden for Ring Fit Adventure into Ryujinx**, allowing Ring Fit Adventure to be played on macOS using Ryujinx with a Ring-Con and Joy-Con controllers.

### How to Use

Open the game's input settings in Ryujinx:

1. Enable **Ring-Con**
2. Configure two players

#### Player 1

| Setting         | Value             |
| --------------- | ----------------- |
| Input Device    | **Right Joy-Con** |
| Controller Type | **Right Joy-Con** |

#### Player 2

| Setting         | Value            |
| --------------- | ---------------- |
| Input Device    | **Left Joy-Con** |
| Controller Type | **Dual Joy-Con** |

After configuring the controllers, you should be able to play Ring Fit Adventure using:

* Ring-Con
* Right Joy-Con
* Left Joy-Con

### Current Status

* ✅ Ring Fit Adventure runs on macOS
* ✅ Ring-Con input is supported
* ✅ Left and right Joy-Con are supported
* ⚠️ **Currently tested only on macOS**
* ⚠️ Windows and Linux have not been fully tested

### Background

This project is based on Ryujinx and ports the Ring-Con input logic related to Ring Fit Adventure from Eden Emulator.

The goal is to combine the strengths of both projects:

> **Eden's Ring-Con support + Ryujinx's macOS game compatibility**

Ryujinx provides much better game compatibility on macOS, while Eden already contains the necessary Ring-Con handling for Ring Fit Adventure.

By porting this functionality into Ryujinx, this project aims to make **Ring Fit Adventure playable on Mac**.

---

## Disclaimer

This project is intended for research, development, and emulator compatibility purposes.

Please use legally obtained game software and controller hardware.
