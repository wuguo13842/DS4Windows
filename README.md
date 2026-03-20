# DISCONTINUATION

**I'm sad to announce that I will be discontinuing any further work on this project.**

I initially wanted to take a short break and come back to the project, 
however with 1) lack of time for development due to both work and personal life and 2) the development of machine learning technologies I'm just not willing
to put any more work into it. Especially the 2nd point made me completely lose interest in it. 

Open source software, something I once valued way more than
proprietary software, has now become a clown show due to so-called _vibe coders_. People's contributions to OSS were once a great learning experience, GitHub 
used to be a place where you could exchange knowledge and build a nice resume as well as find many fantastic hobby projects you could make use of. 
I've learnt that myself. Now, seeing many OSS projects receive many completely trash and meaningless pull requests and GitHub being flooded with very 
low quality or straight up vulnerable on all fronts code, I lost my faith in it. 

My goal with this project was producing good (for obvious reasons not perfect, but still goot) quality code and delivering what people wanted. 
**To learn and become a better software engineer.** All the motivation I had to do that is completely gone now.

Thank you for all the contributors, people who helped me develop it and people who reported bugs and feature requests.

It was a good ride.

Maybe one day we will come back to the way the things were before.

---

## A Message from the New Maintainer

First and foremost, I want to express my deepest respect and gratitude to the original author ([schmaldeo](https://github.com/schmaldeo)) for laying the foundation of this project and for all the hard work put into it. The decision to continue maintaining this project stems from my personal need and passion for learning.

I am a self-taught programming enthusiast. Due to my poor memory and limited English, I often struggle to understand foreign technical materials. AI tools have become invaluable assistants, helping me translate documentation and explain complex concepts. However, I don't just copy-paste mindlessly. My typical workflow is:

1. **Let AI freely implement a feature** – I ask AI to write code based on requirements, without worrying too much about quality at first.
2. **Optimize and understand** – I then study the generated code, ask AI to explain every detail, compare alternatives, and dig into the technical decisions. This is where the real learning happens.
3. **Internalize and refine** – Through this dialogue, I gradually grasp the underlying principles and take control of the code, polishing it into something better.

Yes, this process sometimes produces "garbage code," but with each iteration I learn and move closer to writing clean, maintainable, and efficient code. I see AI as a tool that empowers me to overcome obstacles and contribute meaningfully—not as a replacement for genuine understanding.

I deeply value the experience and insights of seasoned developers. If you have suggestions, critiques, or just want to share knowledge, I am more than eager to learn from you. Whether it's a bug report, a feature request, or a technical discussion, your input will help me write better code and grow as a developer. This is exactly why I created this standalone repository—to have a space where we can openly discuss and learn together through **Issues**.

You can find my active fork here:  
👉 **[DS4Windows (Maintained Fork)](https://github.com/wuguo13842/DS4Windows)**

---

## DS4Windows

Like those other DS4 tools, but sexier.

DS4Windows is an extract anywhere program that allows you to get the best
DualShock 4 experience on your PC. By emulating an Xbox 360 controller, many
more games are accessible. Other input controllers are also supported including the
DualSense, Switch Pro, and JoyCon controllers (**first party hardware only**).

This project is a fork of the work of Jays2Kings and Ryochan7. It adds various new features like switch 
[debouncing](https://www.ganssle.com/debouncing.pdf), a tool that helps to fix stick drift and pitch and roll simulation
for DS3 based on accelerometer value (which is a work of [sunnyqeen](https://github.com/sunnyqeen)).

![DS4Windows Preview](https://raw.githubusercontent.com/Ryochan7/DS4Windows/jay/ds4winwpf_screen_20200412.png)

## About this fork (original author's words)

I've made this fork because some of the buttons on my controller started bouncing. Normally I would just add a
feature that would fix my problem, make a pull request to the original repo and forget about the project. 
The issue here is that Ryochan7 stopped maintaining the original project, so I decided to make slight 
modifications to the code that detects if the installed version is up-to-date, so it now pulls version info from my 
repo. This way if you install my version, you don't get the annoying popup saying your version is outdated. If there 
are any feature requests, I'm more than happy to at least look at them and assess whether I could add them.

*Note: The original author's fork is no longer maintained. The new maintainer has taken over and continues development at [wuguo13842/DS4Windows](https://github.com/wuguo13842/DS4Windows).*

## Versioning

This project follows [Semantic Versioning 2.0.0](https://semver.org/). For the versions available, see the [tags on this repository](https://github.com/wuguo13842/DS4Windows/tags).

## License

DS4Windows is licensed under the terms of the GNU General Public License version 3.
You can find a copy of the terms and conditions of that license at
[https://www.gnu.org/licenses/gpl-3.0.txt](https://www.gnu.org/licenses/gpl-3.0.txt). The license is also
available in this source code from the COPYING file.

## Downloads

- **[Maintained builds of DS4Windows](https://github.com/wuguo13842/DS4Windows/releases)**

## Install

You can install DS4Windows by downloading it from [releases](https://github.com/wuguo13842/DS4Windows/releases) and place it to your preferred place.

Alternatively, you can install [`ds4windows`](https://scoop.sh/#/apps?q=ds4windows&o=true&id=c8b519fcb06da6bb014569fd0a07521839ec5425) via [Scoop](https://scoop.sh/) (may point to the original version; check the source).

Alternatively, you can download the [`ds4w.bat`](https://raw.githubusercontent.com/wuguo13842/DS4Windows/refs/heads/master/ds4w.bat) file and execute it. It will open a window that downloads and places the program in `%LOCALAPPDATA%\DS4Windows` and creates a desktop shortcut to the executable.

## Requirements

- Windows 10 or newer (Thanks Microsoft)
- Microsoft .NET 8.0 Desktop Runtime. [x64](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-8.0.0-windows-x64-installer) or [x86](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-8.0.0-windows-x86-installer)
- Visual C++ 2015-2022 Redistributable. [x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) or [x86](https://aka.ms/vs/17/release/vc_redist.x86.exe)
- [ViGEmBus](https://vigem.org/) driver (DS4Windows will install it for you)
- **Sony** DualShock 4 or other supported controller
- Connection method:
  - Micro USB cable
  - [Sony Wireless Adapter](https://www.amazon.com/gp/product/B01KYVLKG2)
  - Bluetooth 4.0 (via an [adapter like this](https://www.newegg.com/Product/Product.aspx?Item=N82E16833166126) or built in pc). Only use of Microsoft BT stack is supported. CSR BT stack is confirmed to not work with the DS4 even though some CSR adapters work fine using Microsoft BT stack. Toshiba's adapters currently do not work.
  *Disabling 'Enable output data' in the controller profile settings might help with latency issues, but will disable lightbar and rumble support.*
- Disable **PlayStation Configuration Support** and **Xbox Configuration Support** options in Steam