using System.IO;
using System.Text;
using SelectionAssistant.Infrastructure.PowerMonitoring;
using Xunit;

namespace SelectionAssistant.Core.Tests.PowerMonitoring;

public sealed class HttpPowerMonitorClientTests
{
    /// <summary>
    /// 一段缩略但结构真实的 LHM data.json：硬件树 Children → 各 hardware（cpu/gpu/ram/battery）
    /// 的 Sensors 数组，每个 sensor 有 Type/Label/Value/Identifier。本测试覆盖：CPU Package Power +
    /// Cores、GPU Power + 温度 + 频率、Load、Fan、电池（放电负值）。
    /// </summary>
    private const string SampleDataJson = """
    {
      "id": 0,
      "Text": "Sensor",
      "Min": "",
      "Value": "",
      "Max": "",
      "ImageURL": "",
      "Children": [
        {
          "id": 1, "Text": "Intel Core i7-12700K", "Min": "", "Value": "", "Max": "",
          "ImageURL": "images/icon_intel.png",
          "Children": [
            { "id": 12, "Text": "Powers", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 121, "Text": "CPU Package", "Min": "12.0", "Value": "45.3", "Max": "89.1",
                  "ImageURL": "images/power.png", "Type": "Power", "Identifier": "/intelcpu/0/power/0", "Parent": 12, "NodeId": "/intelcpu/0/power/0" },
                { "id": 122, "Text": "CPU Cores", "Min": "5.0", "Value": "30.1", "Max": "70.0",
                  "ImageURL": "images/power.png", "Type": "Power", "Identifier": "/intelcpu/0/power/1", "Parent": 12, "NodeId": "/intelcpu/0/power/1" },
                { "id": 123, "Text": "CPU IA", "Min": "1.0", "Value": "8.2", "Max": "15.0",
                  "ImageURL": "images/power.png", "Type": "Power", "Identifier": "/intelcpu/0/power/3", "Parent": 12, "NodeId": "/intelcpu/0/power/3" }
              ]
            },
            { "id": 13, "Text": "Temperatures", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 131, "Text": "CPU Package", "Min": "30", "Value": "62.5", "Max": "95",
                  "ImageURL": "images/temp.png", "Type": "Temperature", "Identifier": "/intelcpu/0/temperature/0", "NodeId": "/intelcpu/0/temperature/0" }
              ]
            },
            { "id": 14, "Text": "Clocks", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 141, "Text": "Bus Speed", "Min": "100", "Value": "100.0", "Max": "100",
                  "ImageURL": "images/clock.png", "Type": "Clock", "Identifier": "/intelcpu/0/clock/0", "NodeId": "/intelcpu/0/clock/0" }
              ]
            },
            { "id": 15, "Text": "Load", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 151, "Text": "CPU Total", "Min": "1.0", "Value": "23.4", "Max": "100",
                  "ImageURL": "images/load.png", "Type": "Load", "Identifier": "/intelcpu/0/load/0", "NodeId": "/intelcpu/0/load/0" }
              ]
            }
          ]
        },
        {
          "id": 2, "Text": "NVIDIA GeForce RTX 4070", "Min": "", "Value": "", "Max": "",
          "Children": [
            { "id": 22, "Text": "Powers", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 221, "Text": "GPU Power", "Min": "10", "Value": "118.7", "Max": "200",
                  "Type": "Power", "Identifier": "/nvgpu/0/power/0", "NodeId": "/nvgpu/0/power/0" }
              ]
            },
            { "id": 23, "Text": "Temperatures", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 231, "Text": "GPU Core", "Min": "30", "Value": "58.0", "Max": "90",
                  "Type": "Temperature", "Identifier": "/nvgpu/0/temperature/0", "NodeId": "/nvgpu/0/temperature/0" }
              ]
            },
            { "id": 24, "Text": "Clocks", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 241, "Text": "GPU Core", "Min": "210", "Value": "2520.0", "Max": "2700",
                  "Type": "Clock", "Identifier": "/nvgpu/0/clock/0", "NodeId": "/nvgpu/0/clock/0" },
                { "id": 242, "Text": "GPU Memory", "Min": "405", "Value": "6750.0", "Max": "10501",
                  "Type": "Clock", "Identifier": "/nvgpu/0/clock/1", "NodeId": "/nvgpu/0/clock/1" }
              ]
            },
            { "id": 25, "Text": "Load", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 251, "Text": "GPU Core", "Min": "0", "Value": "67.8", "Max": "100",
                  "Type": "Load", "Identifier": "/nvgpu/0/load/0", "NodeId": "/nvgpu/0/load/0" }
              ]
            },
            { "id": 26, "Text": "Fans", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 261, "Text": "GPU Fan", "Min": "0", "Value": "1450", "Max": "3500",
                  "Type": "Fan", "Identifier": "/nvgpu/0/fan/0", "NodeId": "/nvgpu/0/fan/0" }
              ]
            }
          ]
        },
        {
          "id": 3, "Text": "Memory", "Min": "", "Value": "", "Max": "",
          "Children": [
            { "id": 32, "Text": "Powers", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 321, "Text": "Memory", "Min": "1.0", "Value": "2.4", "Max": "8.0",
                  "Type": "Power", "Identifier": "/ram/0/power/0", "NodeId": "/ram/0/power/0" }
              ]
            }
          ]
        },
        {
          "id": 5, "Text": "Battery", "Min": "", "Value": "", "Max": "",
          "Children": [
            { "id": 52, "Text": "Powers", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 521, "Text": "Battery Discharge", "Min": "0", "Value": "-12.5", "Max": "0",
                  "Type": "Power", "Identifier": "/battery/0/power/0", "NodeId": "/battery/0/power/0" }
              ]
            },
            { "id": 53, "Text": "Load", "Min": "", "Value": "", "Max": "",
              "Children": [
                { "id": 531, "Text": "Battery", "Min": "0", "Value": "78.0", "Max": "100",
                  "Type": "Load", "Identifier": "/battery/0/load/0", "NodeId": "/battery/0/load/0" }
              ]
            }
          ]
        }
      ]
    }
    """;

    private static PowerSnapshot Parse(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var snapshot = new PowerSnapshot();
        HttpPowerMonitorClient.ParseDataJson(stream, ref snapshot);
        return snapshot;
    }

    // ── CPU ───────────────────────────────────────────────────────────────

    [Fact]
    public void Parses_CpuPackagePower()
    {
        PowerSnapshot s = Parse(SampleDataJson);
        Assert.Equal(45.3, s.CpuPackageWatts);
    }

    [Fact]
    public void Parses_CpuCoreMaxLoad_WhenAbsent_LeavesNull()
    {
        // Intel 样本里没有 "CPU Core Max" 负载传感器 → 该字段应为 null。
        PowerSnapshot s = Parse(SampleDataJson);
        Assert.Null(s.CpuCoreMaxLoadPct);
    }

    /// <summary>CPU Core Max（最忙核心负载）—— label "CPU Core Max"，必须先于 CPU Total
    /// 抓取（两者都含 "cpu"，顺序错了 Core Max 会被 Total 规则吃掉）。</summary>
    [Fact]
    public void Parses_CpuCoreMaxLoad_TakesBeforeTotal()
    {
        const string coreMaxJson = """
        {
          "Children": [
            { "Text": "CPU", "Children": [
              { "Text": "Load", "Children": [
                { "Text": "CPU Core Max", "Value": "56.3 %", "Type": "Load", "SensorId": "/amdcpu/0/load/1" },
                { "Text": "CPU Total", "Value": "19.0 %", "Type": "Load", "SensorId": "/amdcpu/0/load/0" }
              ]}
            ]}
          ]
        }
        """;
        PowerSnapshot s = Parse(coreMaxJson);
        Assert.Equal(56.3, s.CpuCoreMaxLoadPct);
        Assert.Equal(19.0, s.CpuLoadPct);
    }

    [Fact]
    public void Parses_CpuTemperature()
    {
        Assert.Equal(62.5, Parse(SampleDataJson).CpuTempC);
    }

    [Fact]
    public void Parses_CpuClock()
    {
        Assert.Equal(100.0, Parse(SampleDataJson).CpuClockMhz);
    }

    [Fact]
    public void Parses_CpuLoad()
    {
        Assert.Equal(23.4, Parse(SampleDataJson).CpuLoadPct);
    }

    // ── GPU ───────────────────────────────────────────────────────────────

    [Fact]
    public void Parses_GpuPower()
    {
        Assert.Equal(118.7, Parse(SampleDataJson).GpuPowerWatts);
    }

    [Fact]
    public void Parses_GpuTemperature()
    {
        Assert.Equal(58.0, Parse(SampleDataJson).GpuTempC);
    }

    [Fact]
    public void Parses_GpuCoreClock_And_MemoryClock()
    {
        PowerSnapshot s = Parse(SampleDataJson);
        Assert.Equal(2520.0, s.GpuClockMhz);
        Assert.Equal(6750.0, s.GpuMemClockMhz);
    }

    [Fact]
    public void Parses_GpuLoad()
    {
        Assert.Equal(67.8, Parse(SampleDataJson).GpuLoadPct);
    }

    [Fact]
    public void Parses_GpuFan()
    {
        Assert.Equal(1450, Parse(SampleDataJson).GpuFanRpm);
    }

    // ── System / Battery ─────────────────────────────────────────────────

    [Fact]
    public void Parses_RamPower()
    {
        Assert.Equal(2.4, Parse(SampleDataJson).RamWatts);
    }

    [Fact]
    public void Parses_BatteryDischarge_NegativeValue()
    {
        Assert.Equal(-12.5, Parse(SampleDataJson).BatteryWatts);
    }

    // ── 缺失字段 / 边缘 ─────────────────────────────────────────────────

    [Fact]
    public void Parses_NoBatterySection_LeavesBatteryNull()
    {
        // 台式机版：去掉 Battery 段。
        const string desktopJson = """
        {
          "Children": [
            { "Text": "CPU", "Children": [
              { "Text": "Powers", "Children": [
                { "Text": "CPU Package", "Value": "40.0", "Type": "Power", "Identifier": "/intelcpu/0/power/0" }
              ]}
            ]}
          ]
        }
        """;
        PowerSnapshot s = Parse(desktopJson);
        Assert.Null(s.BatteryWatts);
        Assert.Null(s.GpuPowerWatts);
        Assert.Null(s.Rail12vWatts);
        Assert.Equal(40.0, s.CpuPackageWatts);
    }

    [Fact]
    public void Parses_InvalidNumericValue_LeavesFieldNull()
    {
        const string badValJson = """
        {
          "Children": [
            { "Text": "CPU", "Children": [
              { "Text": "Powers", "Children": [
                { "Text": "CPU Package", "Value": "N/A", "Type": "Power", "Identifier": "/intelcpu/0/power/0" }
              ]}
            ]}
          ]
        }
        """;
        Assert.Null(Parse(badValJson).CpuPackageWatts);
    }

    [Fact]
    public void Parses_DashValue_LeavesFieldNull()
    {
        const string dashJson = """
        {
          "Children": [
            { "Text": "CPU", "Children": [
              { "Text": "Powers", "Children": [
                { "Text": "CPU Package", "Value": "—", "Type": "Power", "Identifier": "/intelcpu/0/power/0" }
              ]}
            ]}
          ]
        }
        """;
        Assert.Null(Parse(dashJson).CpuPackageWatts);
    }

    [Fact]
    public void Parses_EmptyChildren_HandledWithoutCrash()
    {
        const string emptyJson = "{ \"Children\": [] }";
        PowerSnapshot s = Parse(emptyJson);
        // 全空 snapshot，不抛即通过。
        Assert.Null(s.CpuPackageWatts);
    }

    [Fact]
    public void Parses_UsesNameFieldWhenLabelAbsent()
    {
        // LHM 新版某些传感器用 Name 而非 Label。
        const string nameJson = """
        {
          "Children": [
            { "Text": "CPU", "Children": [
              { "Text": "Powers", "Children": [
                { "Name": "CPU Package", "Value": "55.0", "Type": "Power", "NodeId": "/intelcpu/0/power/0" }
              ]}
            ]}
          ]
        }
        """;
        Assert.Equal(55.0, Parse(nameJson).CpuPackageWatts);
    }

    // ── 真实 LHM 格式：Value 带单位 + SensorId 字段 ─────────────────────

    /// <summary>
    /// 真实 LHM 0.9.6 data.json 的 Value 字段<b>带单位</b>（如 "29.3 W"、"62.5 °C"、
    /// "2520.0 MHz"、"1450 RPM"、"19.0 %"）。解析必须剥离单位只取数值。
    /// </summary>
    [Theory]
    [InlineData("29.3 W", 29.3)]
    [InlineData("-12.5 W", -12.5)]
    [InlineData("62.5 °C", 62.5)]
    [InlineData("2520.0 MHz", 2520.0)]
    [InlineData("1450 RPM", 1450.0)]
    [InlineData("19.0 %", 19.0)]
    [InlineData("0.0 W", 0.0)]
    [InlineData("45.3", 45.3)] // 无单位也应正常
    public void Parses_ValueWithUnit_StripsUnitCorrectly(string valueWithUnit, double expected)
    {
        const string tpl = """
        {
          "Children": [
            { "Text": "CPU", "Children": [
              { "Text": "Powers", "Children": [
                { "Text": "Package", "Value": "%V%", "Type": "Power", "SensorId": "/amdcpu/0/power/0" }
              ]}
            ]}
          ]
        }
        """;
        PowerSnapshot s = Parse(tpl.Replace("%V%", valueWithUnit));
        Assert.Equal(expected, s.CpuPackageWatts);
    }

    /// <summary>
    /// 真实 AMD APU 场景（如 Ryzen 7 6800H）：CPU 标签是 "Package"（不是 "CPU Package"），
    /// GPU(iGPU)标签是 "GPU Package"，独立 NVIDIA GPU 在另一节点。SensorId 是稳定路径
    /// （/amdcpu/0、/gpu-amd/0、/gpu-nvidia/0）。验证这套混合硬件被正确分类。
    /// </summary>
    [Fact]
    public void Parses_AmdApuRealWorldFormat()
    {
        const string amdJson = """
        {
          "Children": [
            { "Text": "AMD Ryzen 7 6800H", "Children": [
              { "Text": "Powers", "Children": [
                { "Text": "Package", "Value": "29.3 W", "Type": "Power", "SensorId": "/amdcpu/0/power/0" },
                { "Text": "Core #1 (SMU)", "Value": "1.7 W", "Type": "Power", "SensorId": "/amdcpu/0/power/1" }
              ]},
              { "Text": "Temperatures", "Children": [
                { "Text": "CPU (Tctl)", "Value": "62.5 °C", "Type": "Temperature", "SensorId": "/amdcpu/0/temperature/2" }
              ]},
              { "Text": "Clocks", "Children": [
                { "Text": "Core #1", "Value": "3400.0 MHz", "Type": "Clock", "SensorId": "/amdcpu/0/clock/1" }
              ]},
              { "Text": "Load", "Children": [
                { "Text": "CPU Total", "Value": "19.0 %", "Type": "Load", "SensorId": "/amdcpu/0/load/0" }
              ]}
            ]},
            { "Text": "AMD Radeon Graphics", "Children": [
              { "Text": "Powers", "Children": [
                { "Text": "GPU Package", "Value": "9.7 W", "Type": "Power", "SensorId": "/gpu-amd/0/power/0" }
              ]},
              { "Text": "Load", "Children": [
                { "Text": "GPU Core", "Value": "0.0 %", "Type": "Load", "SensorId": "/gpu-amd/0/load/0" }
              ]}
            ]},
            { "Text": "ASUS Battery", "Children": [
              { "Text": "Powers", "Children": [
                { "Text": "Charge/Discharge Rate", "Value": "0.0 W", "Type": "Power", "SensorId": "/battery/ASUS-Battery_1/power/0" }
              ]}
            ]}
          ]
        }
        """;
        PowerSnapshot s = Parse(amdJson);
        Assert.Equal(29.3, s.CpuPackageWatts);          // "Package" + /amdcpu
        Assert.Null(s.CpuCoreMaxLoadPct);               // AMD 样本无 "CPU Core Max"
        Assert.Equal(62.5, s.CpuTempC);                 // "CPU (Tctl)"
        Assert.Equal(3400.0, s.CpuClockMhz);
        Assert.Equal(19.0, s.CpuLoadPct);
        Assert.Equal(9.7, s.GpuPowerWatts);             // "GPU Package" + /gpu-amd（不能被 CPU 抢）
        Assert.Equal(0.0, s.GpuLoadPct);
        Assert.Equal(0.0, s.BatteryWatts);              // "Charge/Discharge Rate" + /battery
    }

    /// <summary>无 SensorId、无 Identifier、纯 label 时仍能按 label 兜底分类。</summary>
    [Fact]
    public void Parses_NoIdentifier_FallsBackToLabelOnly()
    {
        const string noIdJson = """
        {
          "Children": [
            { "Text": "CPU", "Children": [
              { "Text": "Powers", "Children": [
                { "Text": "CPU Package", "Value": "40.0 W", "Type": "Power" }
              ]}
            ]}
          ]
        }
        """;
        Assert.Equal(40.0, Parse(noIdJson).CpuPackageWatts);
    }

    /// <summary>NVMe/SSD 复合温度（"Composite Temperature"，SensorId /nvme/0）。单盘填 Ssd1TempC。</summary>
    [Fact]
    public void Parses_NvmeCompositeTemperature()
    {
        const string nvmeJson = """
        {
          "Children": [
            { "Text": "Micron_3400_MTFDKBA512TFH", "Children": [
              { "Text": "Temperatures", "Children": [
                { "Text": "Composite Temperature", "Value": "51.0 °C", "Type": "Temperature", "SensorId": "/nvme/0/temperature/0" },
                { "Text": "Temperature #1", "Value": "50.9 °C", "Type": "Temperature", "SensorId": "/nvme/0/temperature/1" }
              ]}
            ]}
          ]
        }
        """;
        PowerSnapshot s = Parse(nvmeJson);
        Assert.Equal(51.0, s.Ssd1TempC);
        Assert.Null(s.Ssd2TempC);
    }

    /// <summary>双 NVMe 盘：/nvme/0 → Ssd1TempC，/nvme/1 → Ssd2TempC。跳过 Warning/Critical 阈值。</summary>
    [Fact]
    public void Parses_TwoNvmeDrives_FillsBothSlots()
    {
        const string dualNvmeJson = """
        {
          "Children": [
            { "Text": "Micron_3400", "Children": [
              { "Text": "Temperatures", "Children": [
                { "Text": "Composite Temperature", "Value": "49.0 °C", "Type": "Temperature", "SensorId": "/nvme/0/temperature/0" },
                { "Text": "Warning Temperature", "Value": "79.0 °C", "Type": "Temperature", "SensorId": "/nvme/0/temperature/10" }
              ]}
            ]},
            { "Text": "GeIL P4A 2TB", "Children": [
              { "Text": "Temperatures", "Children": [
                { "Text": "Composite Temperature", "Value": "51.0 °C", "Type": "Temperature", "SensorId": "/nvme/1/temperature/0" },
                { "Text": "Critical Temperature", "Value": "81.0 °C", "Type": "Temperature", "SensorId": "/nvme/1/temperature/11" }
              ]}
            ]}
          ]
        }
        """;
        PowerSnapshot s = Parse(dualNvmeJson);
        Assert.Equal(49.0, s.Ssd1TempC);
        Assert.Equal(51.0, s.Ssd2TempC);
    }

    /// <summary>内存条温度（"DIMM #1"，SensorId /memory/dimm）。</summary>
    [Fact]
    public void Parses_DimmMemoryTemperature()
    {
        const string ramJson = """
        {
          "Children": [
            { "Text": "Memory", "Children": [
              { "Text": "Temperatures", "Children": [
                { "Text": "DIMM #1", "Value": "55.3 °C", "Type": "Temperature", "SensorId": "/memory/dimm/1/temperature/0" }
              ]}
            ]}
          ]
        }
        """;
        Assert.Equal(55.3, Parse(ramJson).RamTempC);
    }

    /// <summary>无 NVMe/SSD 传感器时 Ssd1TempC/Ssd2TempC 保持 null（台式机老机械盘等）。</summary>
    [Fact]
    public void Parses_NoStorageSensor_LeavesSsdTempNull()
    {
        const string noStorageJson = """
        {
          "Children": [
            { "Text": "CPU", "Children": [
              { "Text": "Powers", "Children": [
                { "Text": "Package", "Value": "30.0 W", "Type": "Power", "SensorId": "/amdcpu/0/power/0" }
              ]}
            ]}
          ]
        }
        """;
        PowerSnapshot s = Parse(noStorageJson);
        Assert.Null(s.Ssd1TempC);
        Assert.Null(s.Ssd2TempC);
    }
}
