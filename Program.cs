using AutoSkill;
using System;
using System.IO;

string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoSkill");
if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
Environment.CurrentDirectory = appData;

Renderer renderer = new Renderer();
await renderer.Start();
