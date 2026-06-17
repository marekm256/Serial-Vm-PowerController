# Serial VM Power Controller

Windows Service plus WPF control panel for a Windows 11 host. The service can start the VMware XP virtual machine in KVM mode, watches a serial CTS signal and shuts down the VM before shutting down the host. The GUI configures the controller, installs/removes the service, starts/stops it and shows service/CTS status.

Default setup:

- COM port: `COM3`
- VMware Workstation tools are expected in the default path: `C:\Program Files (x86)\VMware\VMware Workstation`
- VMX file: select the target `.vmx` file in the GUI for each deployment.
- Soft stop timeout: `60` seconds
- Hard stop timeout: `30` seconds
- Block CTS shutdown: enabled by default

Startup sequence:

1. Windows Service starts.
2. If enabled, the service waits for an active user session.
3. The service runs `vmware-kvm.exe "<vmx>"` in that interactive session.
4. The service opens the configured COM port and monitors CTS.

Shutdown sequence:

1. CTS changes to ON.
2. If `Block CTS shutdown` is enabled, CTS is logged and no shutdown is started.
3. The app waits for the debounce time.
4. The app runs `vmrun stop "<vmx>" soft`.
5. If `vmrun` hangs, the app kills that `vmrun` process after the soft timeout.
6. If the VM is still listed by `vmrun list`, the app runs `vmrun stop "<vmx>" hard`.
7. If enabled, the app schedules `shutdown.exe /s` for the host.

Settings, logs and runtime status are stored in the same folder as `SerialVmPowerController.exe`:

```text
settings.xml
log.txt
runtime-status.xml
```

Copy the whole `Service` folder to the final PC and run the application from that folder. With UWF enabled, commit this folder or add an exclusion if settings and logs must survive restarts.

## Service mode

Run `SerialVmPowerController.exe` normally to open the GUI.

From the GUI, run as administrator and use:

- `Install service` to register the current executable as `SerialVmPowerController`.
- `Start` to start the Windows Service.
- `Stop` to stop the Windows Service.
- `Uninstall` to remove the service.

The service executable path is installed as:

```text
SerialVmPowerController.exe --service
```

Important VMware note: the service is installed under the default Windows service account. On VMware Workstation/KVM setups, test that `vmrun.exe` launched from the service can see and control the same VM that runs in the interactive user session. Any failure is written to `log.txt`.

KVM startup note: `vmware-kvm.exe` is an interactive desktop application. The service starts it in the active console user's session, not in hidden Session 0. Automatic Windows logon should be enabled on the panel if the VM must appear immediately after boot.

## Build

Use Visual Studio 2022 or MSBuild:

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" .\SerialVmPowerController.sln /p:Configuration=Release
```

The Release build also creates `SerialVmPowerController.xml` next to the executable. Visual Studio uses this file for IntelliSense/reference documentation.

## Service Folder

Create the portable deployment folder:

```powershell
.\Create-ServiceFolder.ps1
```

The script resolves the git repository root, builds Release and copies the runtime files plus `README.md` into `Service` in the repository root.

