using Daikin2Mqtt.Daikin;

namespace Daikin2Mqtt.Tests;

public class DaikinResponseParserTests
{
    [Fact]
    public void Parse_ReadsKeyValuePairs()
    {
        var r = DaikinResponseParser.Parse("ret=OK,htemp=24.0,hhum=-,otemp=15.0,err=0,cmpfreq=999");

        Assert.True(r.IsOk);
        Assert.Equal("24.0", r.Values["htemp"]);
        Assert.Equal("-", r.Values["hhum"]);
        Assert.Equal("999", r.Values["cmpfreq"]);
    }

    [Fact]
    public void Parse_DecodesUrlEncodedValues()
    {
        var r = DaikinResponseParser.Parse("ret=OK,type=aircon,name=%44%61%69%6b%69%6e,icon=0");

        Assert.Equal("Daikin", r.Values["name"]);
        Assert.Equal("0", r.Values["icon"]);
    }

    [Fact]
    public void Parse_DecodesUtf8AndKeepsInvalidEscapes()
    {
        var r = DaikinResponseParser.Parse("ret=OK,name=Salon%20%C3%A9t%C3%A9,pct=50%");

        Assert.Equal("Salon été", r.Values["name"]);
        Assert.Equal("50%", r.Values["pct"]);
    }

    [Fact]
    public void Parse_KeepsEmptyValues()
    {
        var r = DaikinResponseParser.Parse("ret=OK,pow=0,mode=3,adv=,stemp=25.0");

        Assert.Equal(string.Empty, r.Values["adv"]);
        Assert.Equal("25.0", r.Values["stemp"]);
    }

    [Fact]
    public void Parse_MondataKeepsEqualsAndCommas()
    {
        var r = DaikinResponseParser.Parse("ret=OK,mondata=pv=2,cpv=2,cpv_minor=00,mac=xxxxxxxxxxxx,mode=33,pow=30");

        Assert.Equal("pv=2,cpv=2,cpv_minor=00,mac=xxxxxxxxxxxx,mode=33,pow=30", r.Values["mondata"]);
        Assert.False(r.Values.ContainsKey("pv"));
    }

    [Theory]
    [InlineData("ret=PARAM NG", "PARAM NG")]
    [InlineData("ret=ADV_NG", "ADV_NG")]
    [InlineData("ret=NG,msg=x", "NG")]
    public void Parse_ReportsErrorsWithoutOk(string text, string ret)
    {
        var r = DaikinResponseParser.Parse(text);

        Assert.False(r.IsOk);
        Assert.Equal(ret, r.Ret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>404 not found</html>")]
    public void Parse_NotADaikinAnswerHasNoRet(string? text)
    {
        var r = DaikinResponseParser.Parse(text);

        Assert.Equal(string.Empty, r.Ret);
        Assert.False(r.IsOk);
    }

    [Fact]
    public void Parse_ToleratesLineBreaksAndStrayCommas()
    {
        var r = DaikinResponseParser.Parse("ret=OK,pow=1,\r\nmode=4,,stemp=21.5,\r\n");

        Assert.Equal("1", r.Values["pow"]);
        Assert.Equal("4", r.Values["mode"]);
        Assert.Equal("21.5", r.Values["stemp"]);
    }

    [Theory]
    [InlineData("22.5", 22.5)]
    [InlineData("-3", -3.0)]
    [InlineData("M", null)]
    [InlineData("--", null)]
    [InlineData("-", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseNumber_UnderstandsThePlaceholders(string? text, double? expected) =>
        Assert.Equal(expected, DaikinResponseParser.ParseNumber(text));

    [Fact]
    public void ParseSlashList_SplitsValues() =>
        Assert.Equal(new[] { 1.0, 0, 25 }, DaikinResponseParser.ParseSlashList("1/-/25"));
}

public class DaikinValuesTests
{
    public static IEnumerable<object[]> HaModes() => new[] { "heat_cool", "dry", "cool", "heat", "fan_only" }.Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(HaModes))]
    public void Modes_RoundTripFromHomeAssistant(string ha) =>
        Assert.Equal(ha, DaikinValues.ModeToHa(DaikinValues.ModeToDaikin(ha)));

    [Theory]
    [InlineData("0", "heat_cool")]
    [InlineData("1", "heat_cool")]
    [InlineData("7", "heat_cool")]
    [InlineData("2", "dry")]
    [InlineData("3", "cool")]
    [InlineData("4", "heat")]
    [InlineData("6", "fan_only")]
    public void Modes_AreReadFromTheUnit(string daikin, string ha)
    {
        Assert.Equal(ha, DaikinValues.ModeToHa(daikin));
        // Whatever the unit reported, the canonical value sent back means the same.
        Assert.Equal(ha, DaikinValues.ModeToHa(DaikinValues.ModeToDaikin(ha)));
    }

    [Fact]
    public void Modes_AutoIsSentAsOne()
    {
        Assert.Equal("1", DaikinValues.ModeToDaikin("heat_cool"));
        Assert.Null(DaikinValues.ModeToDaikin("off"));
        Assert.Null(DaikinValues.ModeToHa("5"));
    }

    [Theory]
    [InlineData(true, "3", "cool")]
    [InlineData(false, "3", "off")]
    [InlineData(null, "4", "heat")]
    public void HvacMode_IsOffWhenThePowerIsOff(bool? power, string mode, string expected) =>
        Assert.Equal(expected, DaikinValues.HvacMode(power, mode));

    [Fact]
    public void Fan_MapsEverySpeedBothWays()
    {
        var expected = new Dictionary<string, string> { ["auto"] = "A", ["quiet"] = "B", ["1"] = "3", ["2"] = "4", ["3"] = "5", ["4"] = "6", ["5"] = "7" };

        Assert.Equal(expected.Keys.Order(), DaikinValues.FanModes.Order());
        foreach (var (ha, daikin) in expected)
        {
            Assert.Equal(daikin, DaikinValues.FanToDaikin(ha));
            Assert.Equal(ha, DaikinValues.FanToHa(daikin));
        }
    }

    [Fact]
    public void Swing_MapsEveryDirectionBothWays()
    {
        var expected = new Dictionary<string, string> { ["off"] = "0", ["vertical"] = "1", ["horizontal"] = "2", ["both"] = "3" };

        foreach (var (ha, daikin) in expected)
        {
            Assert.Equal(daikin, DaikinValues.SwingToDaikin(ha));
            Assert.Equal(ha, DaikinValues.SwingToHa(daikin));
        }

        Assert.Null(DaikinValues.SwingToHa("9"));
    }

    [Theory]
    [InlineData("none", "")]
    [InlineData("powerful", "2")]
    [InlineData("eco", "12")]
    [InlineData("streamer", "13")]
    public void Presets_MapBothWays(string ha, string adv)
    {
        Assert.Equal(adv, DaikinValues.PresetToDaikin(ha));
        Assert.Equal(ha, DaikinValues.PresetToHa(adv));
    }

    [Theory]
    [InlineData("2/13", "powerful")]
    [InlineData("12/13", "eco")]
    [InlineData(null, "none")]
    [InlineData("99", null)]
    public void Presets_HandleCombinedAndUnknownValues(string? adv, string? expected) =>
        Assert.Equal(expected, DaikinValues.PresetToHa(adv));

    [Theory]
    [InlineData(false, "4", 42.0, "off")]
    [InlineData(true, "4", 42.0, "heating")]
    [InlineData(true, "4", 0.0, "idle")]
    [InlineData(true, "3", 30.0, "cooling")]
    [InlineData(true, "2", 30.0, "drying")]
    [InlineData(true, "6", 0.0, "fan")]
    [InlineData(true, "4", null, null)]
    public void HvacAction_IsDeducedFromTheCompressor(bool power, string mode, double? hz, string? expected) =>
        Assert.Equal(expected, DaikinValues.HvacAction(power, mode, hz, 20, 22));

    [Theory]
    [InlineData(18.0, 22.0, "heating")]
    [InlineData(25.0, 22.0, "cooling")]
    public void HvacAction_AutoModeFollowsTheRoomTemperature(double room, double target, string expected) =>
        Assert.Equal(expected, DaikinValues.HvacAction(true, "1", 40, room, target));

    [Fact]
    public void HvacModes_ListOffFirst() =>
        Assert.Equal(new[] { "off", "heat_cool", "dry", "cool", "heat", "fan_only" }, DaikinValues.HvacModes);
}

public class DaikinControlRequestTests
{
    private static Dictionary<string, string> Current(string pow = "1", string mode = "3", string stemp = "22.0") => new()
    {
        ["pow"] = pow, ["mode"] = mode, ["stemp"] = stemp, ["shum"] = "0", ["f_rate"] = "A", ["f_dir"] = "0",
        ["dt1"] = "23.0", ["dt3"] = "24.5", ["dt4"] = "20.0", ["dt7"] = "23.0",
    };

    [Fact]
    public void Build_AlwaysSendsTheSixParametersInOrder()
    {
        var request = DaikinControlRequest.Build(Current(), new DaikinCommand { Temperature = 23.5 });

        Assert.Equal("pow=1&mode=3&stemp=23.5&shum=0&f_rate=A&f_dir=0", request.ToQuery());
    }

    [Fact]
    public void Build_ChangesOnlyTheRequestedField()
    {
        var current = Current();

        Assert.Equal("pow=0&mode=3&stemp=22.0&shum=0&f_rate=A&f_dir=0", DaikinControlRequest.Build(current, new DaikinCommand { Power = false }).ToQuery());
        Assert.Equal("pow=1&mode=3&stemp=22.0&shum=0&f_rate=5&f_dir=0", DaikinControlRequest.Build(current, new DaikinCommand { FanRate = "5" }).ToQuery());
        Assert.Equal("pow=1&mode=3&stemp=22.0&shum=0&f_rate=A&f_dir=3", DaikinControlRequest.Build(current, new DaikinCommand { SwingDirection = "3" }).ToQuery());
    }

    [Fact]
    public void Build_DryModeHasNoTemperature()
    {
        var request = DaikinControlRequest.Build(Current(), new DaikinCommand { Mode = "2", Temperature = 20 });

        Assert.Equal("M", request.Stemp);
        Assert.Equal("pow=1&mode=2&stemp=M&shum=0&f_rate=A&f_dir=0", request.ToQuery());
    }

    [Fact]
    public void Build_FanModeHasNoTemperature()
    {
        var request = DaikinControlRequest.Build(Current(), new DaikinCommand { Mode = "6" });

        Assert.Equal("--", request.Stemp);
        Assert.Equal("pow=1&mode=6&stemp=--&shum=0&f_rate=A&f_dir=0", request.ToQuery());
    }

    [Fact]
    public void Build_StaysInDryModeWithoutTemperature()
    {
        // The unit answers stemp=M in Dry mode: a power or fan change must keep sending M.
        var request = DaikinControlRequest.Build(Current(mode: "2", stemp: "M"), new DaikinCommand { FanRate = "B" });

        Assert.Equal("pow=1&mode=2&stemp=M&shum=0&f_rate=B&f_dir=0", request.ToQuery());
    }

    [Theory]
    [InlineData("3", "24.5")] // dt3: cooling
    [InlineData("4", "20.0")] // dt4: heating
    [InlineData("1", "23.0")] // dt1: auto
    [InlineData("7", "23.0")] // dt7: auto (alternative)
    public void Build_ChangingTheModeRestoresTheTemperatureStoredForIt(string mode, string expected)
    {
        var request = DaikinControlRequest.Build(Current(mode: "6", stemp: "--"), new DaikinCommand { Mode = mode });

        Assert.Equal(expected, request.Stemp);
    }

    [Fact]
    public void Build_LeavingDryModeWithoutStoredTemperatureFallsBackToTheCurrentOne()
    {
        var current = new Dictionary<string, string> { ["pow"] = "1", ["mode"] = "2", ["stemp"] = "M" };

        Assert.Equal("22.0", DaikinControlRequest.Build(current, new DaikinCommand { Mode = "3" }).Stemp);
    }

    [Fact]
    public void Build_ChangingModeKeepsTheCurrentTemperatureWhenNoneIsStored()
    {
        var current = new Dictionary<string, string> { ["pow"] = "1", ["mode"] = "3", ["stemp"] = "26.0" };

        Assert.Equal("26.0", DaikinControlRequest.Build(current, new DaikinCommand { Mode = "4" }).Stemp);
    }

    [Fact]
    public void Build_ExplicitTemperatureWinsOverTheStoredOne()
    {
        var request = DaikinControlRequest.Build(Current(mode: "6", stemp: "--"), new DaikinCommand { Mode = "4", Temperature = 19 });

        Assert.Equal("19.0", request.Stemp);
    }

    [Fact]
    public void Build_FillsMissingValuesWithSafeDefaults()
    {
        var request = DaikinControlRequest.Build(new Dictionary<string, string>(), new DaikinCommand { Power = true });

        Assert.Equal("pow=1&mode=3&stemp=22.0&shum=0&f_rate=A&f_dir=0", request.ToQuery());
    }

    [Fact]
    public void Build_KeepsTheTargetHumidityOfTheUnit()
    {
        var current = Current();
        current["shum"] = "AUTO";

        Assert.Equal("AUTO", DaikinControlRequest.Build(current, new DaikinCommand { Power = false }).Shum);
    }
}

public class DaikinCapabilitiesTests
{
    private static Dictionary<string, string> Model(params (string Key, string Value)[] values)
    {
        var model = new Dictionary<string, string>
        {
            ["model"] = "0B66", ["type"] = "N", ["humd"] = "0", ["s_humd"] = "0", ["temp"] = "1", ["en_frate"] = "1", ["en_fdir"] = "1",
            ["s_fdir"] = "1", ["en_spmode"] = "0", ["en_onofftmr"] = "1",
        };
        foreach (var (key, value) in values)
        {
            model[key] = value;
        }

        return model;
    }

    [Fact]
    public void Parse_ReadsTheDocumentedExample()
    {
        var caps = DaikinCapabilities.Parse(Model());

        Assert.True(caps.FanRate);
        Assert.True(caps.Swing);
        Assert.True(caps.IndoorTemperature);
        Assert.True(caps.OnOffTimer);
        Assert.False(caps.HumidityControl);
        Assert.False(caps.HumiditySensor);
        Assert.False(caps.SpecialMode);
        Assert.Equal("0B66", caps.Model);
    }

    [Fact]
    public void Parse_ReadsTheFlagsOfAFullUnit()
    {
        var caps = DaikinCapabilities.Parse(Model(("humd", "1"), ("s_humd", "1"), ("en_spmode", "1"), ("en_frate", "0"), ("s_fdir", "0"), ("temp", "0")));

        Assert.True(caps.HumidityControl);
        Assert.True(caps.HumiditySensor);
        Assert.True(caps.SpecialMode);
        Assert.False(caps.FanRate);
        Assert.False(caps.Swing);
        Assert.False(caps.IndoorTemperature);
    }

    [Fact]
    public void Parse_MissingKeysAssumeAPlainUnit()
    {
        var caps = DaikinCapabilities.Parse(new Dictionary<string, string>());

        Assert.True(caps.FanRate);
        Assert.True(caps.Swing);
        Assert.True(caps.IndoorTemperature);
        Assert.False(caps.HumiditySensor);
        Assert.False(caps.SpecialMode);
    }

    [Fact]
    public void Parse_SwingFallsBackOnEnFdirWhenThereIsNoSFdir()
    {
        var model = Model(("en_fdir", "0"));
        model.Remove("s_fdir");

        Assert.False(DaikinCapabilities.Parse(model).Swing);
    }

    [Fact]
    public void Parse_TemperatureBoundsComeFromTheUnitOptionsOrTheDefaults()
    {
        Assert.Equal(10, DaikinCapabilities.Parse(Model()).MinTemperature);
        Assert.Equal(32, DaikinCapabilities.Parse(Model()).MaxTemperature);

        var custom = DaikinCapabilities.Parse(Model(), new DaikinUnitOptions { MinTemperature = 16, MaxTemperature = 30 });
        Assert.Equal(16, custom.MinTemperature);
        Assert.Equal(30, custom.MaxTemperature);

        var invalid = DaikinCapabilities.Parse(Model(), new DaikinUnitOptions { MinTemperature = 30, MaxTemperature = 16 });
        Assert.Equal(10, invalid.MinTemperature);
    }
}

public class DaikinEnergyTests
{
    private static IReadOnlyDictionary<string, string> Parse(string text) => DaikinResponseParser.Parse(text).Values;

    [Fact]
    public void Parse_ConvertsHundredsOfWattHoursToKwh()
    {
        var energy = DaikinEnergy.Parse(
            Parse("ret=OK,today_runtime=146,datas=10/20/0/0/0/0/30"),
            Parse("ret=OK,curr_day_heat=0/0/5/5/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0,prev_1day_heat=0/0,curr_day_cool=10/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0/0,prev_1day_cool=0"),
            Parse("ret=OK,curr_month_heat=50/50,prev_month_heat=0,curr_month_cool=100/0,prev_month_cool=0"),
            Parse("ret=OK,curr_year_heat=100/200,prev_year_heat=0,curr_year_cool=300,prev_year_cool=0"));

        Assert.Equal(146, energy.TodayRuntime);
        Assert.Equal(6.0, energy.Week);
        Assert.Equal(1.0, energy.TodayHeat);
        Assert.Equal(1.0, energy.TodayCool);
        Assert.Equal(2.0, energy.Today);
        Assert.Equal(10.0, energy.MonthHeat);
        Assert.Equal(20.0, energy.Month);
        Assert.Equal(30.0, energy.YearHeat);
        Assert.Equal(30.0, energy.YearCool);
        Assert.Equal(60.0, energy.Year);
    }

    [Fact]
    public void Parse_MissingEndpointsGiveNoValue()
    {
        var energy = DaikinEnergy.Parse(Parse("ret=OK,today_runtime=5,datas=0/0/0/0/0/0/0"), null, null, null);

        Assert.Equal(5, energy.TodayRuntime);
        Assert.Equal(0.0, energy.Week);
        Assert.Null(energy.Today);
        Assert.Null(energy.Month);
        Assert.Null(energy.Year);
    }

    [Fact]
    public void Parse_YearFallsBackOnThePlainTotal()
    {
        var energy = DaikinEnergy.Parse(null, null, null, Parse("ret=OK,previous_year=0/0,this_year=10/20/30"));

        Assert.Equal(6.0, energy.Year);
        Assert.Null(energy.YearHeat);
    }
}
