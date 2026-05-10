using System;

namespace ai02;

internal static class FrontlineKnowledgeBase
{
    private static readonly FrontlineKnowledgeRuleSnapshot[] GlobalRules =
    {
        new(
            "global.respawn.unlimited",
            "前线无限复活",
            "玩家在纷争前线中被击倒并陷入无法战斗后，会自动从己方出生点重生，复活没有次数限制。",
            new[] { "通用", "复活", "节奏" }),
        new(
            "global.spawn.invulnerability.safe_zone",
            "出生点无限无敌",
            "玩家位于己方出生点时会获得无限时间的无敌状态，攻击无法造成伤害，也不会受到牵引、击退等效果和摔落伤害。",
            new[] { "通用", "出生点", "无敌", "安全区" }),
        new(
            "global.spawn.invulnerability.after_leave",
            "离开出生点后 10 秒无敌",
            "离开出生点后，出生点无敌会转为持续 10 秒后消失的无敌状态；这段时间内目标仍不适合作为集火对象。",
            new[] { "通用", "出生点", "无敌", "10秒" }),
        new(
            "global.spawn.fall_damage_transition",
            "出生点离开瞬间的摔落伤害",
            "从出生点离开时，部分区域摔下造成的伤害发生在无限持续时间无敌转为 10 秒无敌的交替瞬间。",
            new[] { "通用", "出生点", "无敌", "摔落" }),
        new(
            "global.map.home_base_marker",
            "己方出生点地图标识",
            "己方出生点在地图上会显示星形标记，并带有阵营对应颜色的六边形高亮。",
            new[] { "通用", "出生点", "地图标识" }),
        new(
            "global.return.combat_available",
            "前线返回可战斗中使用",
            "纷争前线中可以使用通用技能返回回到己方起始点；返回在战斗中也可以使用，并且没有复唱时间。",
            new[] { "通用", "返回", "撤退", "出生点" }),
        new(
            "global.return.interrupt",
            "返回读条会被攻击打断",
            "咏唱返回时受到攻击会被打断；但学者的蛊毒法、机工士附加的野火等效果不会打断返回咏唱。",
            new[] { "通用", "返回", "打断", "咏唱" }),
        new(
            "global.mount.frontline",
            "前线地面坐骑规则",
            "玩家可以骑乘自身持有的任意坐骑在地面移动，战斗状态中也可以骑乘；无法飞行，无法共同骑乘他人坐骑，所有坐骑移动速度相同。",
            new[] { "通用", "坐骑", "移动", "转点" }),
        new(
            "global.mount.dismount_lockout",
            "被攻击会强制下马并锁骑乘",
            "骑乘坐骑时受到攻击会被强制解除坐骑，并进入 5 秒不可骑乘状态。",
            new[] { "通用", "坐骑", "拦截", "5秒" }),
        new(
            "global.battle_high.core",
            "斗志昂扬是前线发育核心",
            "战意达到阈值后会获得斗志昂扬强化；自己击倒敌人、协助小队队员击倒敌人都会增加战意。",
            new[] { "通用", "战意", "斗志昂扬", "发育" }),
        new(
            "global.battle_high.version_75",
            "7.5 后倒地不损失战意",
            "版本 7.5 起陷入无法战斗状态不再损失战意；但在出生地切换职业或掉线重连会导致战意清空。",
            new[] { "通用", "战意", "版本7.5", "切职" }),
        new(
            "global.battle_high.bonus",
            "斗志昂扬提升伤害和治疗",
            "战意最多 100，每 20 点提升 1 级斗志昂扬；斗志昂扬会提高自身发动攻击造成的伤害和自身发动的体力恢复效果，最高 50%。",
            new[] { "通用", "战意", "斗志昂扬", "伤害", "治疗" }),
        new(
            "global.battle_high.limit_break",
            "战意影响极限技伤害",
            "战意可以提高极限技伤害，但造成生命值比例伤害和即死效果除外；有玩家达到斗志昂扬 V 时会向战局播报所在阵营。",
            new[] { "通用", "战意", "极限技", "播报" }),
        new(
            "global.damage_formula",
            "前线伤害与治疗计算要考虑战意和补正",
            "实际伤害主要由面板伤害、自身增减益、斗志昂扬等级、职业补正、目标增减益共同决定；当前前线浮动值极小，基本可忽略。",
            new[] { "通用", "伤害计算", "治疗计算", "职业补正" }),
        new(
            "global.limit_break.personal_gauge",
            "玩家对战极限技是个人槽",
            "玩家对战中的极限技不是小队共享，而是以个人计算；极限槽随时间积累，蓄满后即可使用当前职业极限技。",
            new[] { "通用", "极限技", "个人资源" }),
        new(
            "global.limit_break.rank_speed",
            "阵营排名影响极限技积蓄速度",
            "阵营第一名极限技积蓄速度减速 25%，第二名正常，第三名加速 25%；多家同分时按排名靠前、速度较慢的规则计算，开局三家 0 分均视为第一名。",
            new[] { "通用", "极限技", "排名", "比分" }),
        new(
            "global.limit_break.reset",
            "死亡暂停极限槽，切职和重连归零",
            "被击倒时极限槽暂停积蓄；中途切换职业或掉线重连会使极限技槽归零。极限技伤害计算方式和普通技能相同，比例伤害和即死类除外。",
            new[] { "通用", "极限技", "死亡", "切职" }),
        new(
            "global.crowd_control.duration",
            "前线控制持续时间缩短",
            "纷争前线内眩晕、加重、止步、沉默、睡眠、冻结的持续时间会缩短 25%。",
            new[] { "通用", "控制", "眩晕", "沉默", "冻结" }),
        new(
            "global.frontline.profile",
            "前线是三方 72 人大规模对战",
            "纷争前线由三方大国防联军参与争夺；各阵营由三支满编小队组成 24 人团队，最多 72 人同时参战。",
            new[] { "通用", "战场规模", "三方", "72人" }),
    };

    private static readonly FrontlineBattlefieldProfileSnapshot BattlefieldProfile = new(
        3,
        3,
        24,
        72,
        "三方阵营对抗；每方三支满编小队，共 24 人，最多 72 人同时参战。");

    private static readonly FrontlineCommanderMacroIntentSnapshot[] CommanderMacroIntents =
    {
        new(
            "macro.intent.pincer_warning",
            "夹击/空降警告",
            "两家压力或高台落点正在形成，立即降低深入程度。",
            new[] { "夹击", "四小", "空降", "高台空降" },
            "这是最高优先级风险宏之一，核心不是后退，而是先脱出夹角或横向散开，再决定回头反打。",
            "映射到第三方夹击、高台落点、被包围和撤退路线风险；风险致命时给撤出夹角，风险可控时给回头清侧。",
            new[] { "宏意图", "夹击", "高台", "撤退", "反打" }),
        new(
            "macro.intent.focus_target",
            "集火目标",
            "把所有伤害压到当前目标，尤其是低血、被控、落单或高价值职业。",
            new[] { "集火", "<t>", "targetjob", "标一", "这个" },
            "集火宏通常追求极短阅读时间；目标选择应优先看可杀性、被控、职业硬度、防御状态和我方火力样本。",
            "映射到屏幕集火目标、单点倒计时和目标保护/转火逻辑；不做自动发宏。",
            new[] { "宏意图", "集火", "单点", "目标选择" }),
        new(
            "macro.intent.return_reset",
            "返回重整",
            "撤退不等于走回去；能读返回时直接回出生点重整。",
            new[] { "返回", "通用技能返回", "回家", "重整" },
            "返回无复唱且战斗中可用，但被直接攻击会打断；适合少人、散线、追击压力低或需要快速会合时使用。",
            "映射到少人/散线/不可接团时的重整指令；贴脸压力高时不建议把返回当作稳定逃生。",
            new[] { "宏意图", "返回", "重整", "撤退" }),
        new(
            "macro.intent.resource_refresh",
            "资源刷新提醒",
            "刷点、飞机、冰、怪物或地图资源即将改变战局。",
            new[] { "资源刷新", "刷点", "飞机", "冰", "看看地图" },
            "这类宏不是信息提示而是节奏切换：停止无收益缠斗，提前压下一资源或打断对方白拿。",
            "映射到下一资源倒计时、目标优先级和提前压位；避免继续为低尾分/零散击杀拖住主团。",
            new[] { "宏意图", "资源", "转点", "地图" }),
        new(
            "macro.intent.follow_commander",
            "跟随指挥",
            "队伍需要收束到同一移动方向，而不是各自判断。",
            new[] { "跟", "跟随", "看指挥在哪", "跟紧" },
            "跟随宏解决的是队形熵增；它经常比具体目标更重要，因为散线会让集火、撤退和反打全部失效。",
            "映射到凝聚度、跟随率和旅行指令；只在队伍散或需要换线时提高权重。",
            new[] { "宏意图", "跟随", "队形", "转线" }),
        new(
            "macro.intent.retreat_disengage",
            "撤退/收追",
            "当前收益不够覆盖继续深入的风险。",
            new[] { "撤退", "先撤退", "该走了", "别追了", "不要深追" },
            "撤退宏常见触发是被夹、尾追、位置差、敌方反打窗口或已打出收益后该收。",
            "映射到深追陷阱、撤退路线、第三方夹击和击杀后收手；优先使用短指挥文案。",
            new[] { "宏意图", "撤退", "收追", "风险" }),
        new(
            "macro.intent.aoe_countdown",
            "AOE/三角倒计时",
            "让群体爆发在同一秒压到同一点。",
            new[] { "AOE准备", "三角", "三秒", "3", "2", "1", "杀" },
            "倒计时宏的本质是同步爆发，不是持续喊打；只在敌方被控、聚集或我方关键技能/极限技窗口足够时有价值。",
            "映射到控制窗口、关键技能就绪、低血密度和集火倒计时；系统只显示短倒计时意图。",
            new[] { "宏意图", "AOE", "倒计时", "爆发", "集火" }),
        new(
            "macro.intent.height_reposition",
            "高台上下移动",
            "利用高度差切入、脱离或躲避落点爆发。",
            new[] { "跳高台", "上高台", "下高台", "高台" },
            "高台宏是地形指令，重点是不要在落点、窄口或断崖边站桩；上下高台都要结合撤退路线。",
            "映射到高台空降风险、地形风险和绕路/横向展开指令。",
            new[] { "宏意图", "高台", "地形", "位移" }),
        new(
            "macro.intent.interrupt_touch",
            "打断摸点",
            "阻止敌方完成占领或交互，让对方不能白拿分。",
            new[] { "打断摸点", "打断", "摸点" },
            "打断宏优先级通常高于追击零散目标；能打断一秒就可能改变目标归属。",
            "映射到争夺中目标、敌方占点压力和控制/直伤可用性。",
            new[] { "宏意图", "打断", "摸点", "目标" }),
        new(
            "macro.intent.split_touch",
            "分队摸点/打野",
            "少量人员处理低风险副目标，主团保留节奏。",
            new[] { "摸点", "打野", "打小冰", "1-4人", "3-4个" },
            "分队宏不是主团拆散；它只适合低风险副目标、敌方主团远离或主团当前不能离开时。",
            "映射到次级目标、距离、风险和主团凝聚度；避免在被夹/少人时拆散。",
            new[] { "宏意图", "分队", "摸点", "小冰" }),
        new(
            "macro.intent.defend_objective",
            "守点",
            "已有收益需要留少量人防偷，主团不一定停在点上。",
            new[] { "守点", "留人守点", "防偷" },
            "守点宏强调人数克制：多留会丢主团节奏，少留会白给目标。",
            "映射到已控制目标、剩余时间、敌方小队靠近和主团下一目标价值。",
            new[] { "宏意图", "守点", "防守", "人数分配" }),
        new(
            "macro.intent.counter_engage",
            "准备反打",
            "先接住敌方进场，等技能交出后回头压。",
            new[] { "准备战斗", "反打", "准备上", "回头反打" },
            "反打宏需要队伍不要继续逃散；前排顶住、后排同一目标、等敌方爆发空掉。",
            "映射到敌方开团意图、敌方关键技能使用、我方控制/回复窗口。",
            new[] { "宏意图", "反打", "接团", "开团" }),
        new(
            "macro.intent.big_ice_all_in",
            "全力大冰",
            "碎冰战里高价值大冰需要主团集中火力快速转化分数。",
            new[] { "全力大冰", "大冰" },
            "大冰宏强调不要分火；大冰期间分散追人会导致贡献不足或被第三方抢走价值。",
            "映射到碎冰高价值冰、血量/贡献速度、敌方压近风险。",
            new[] { "宏意图", "碎冰", "大冰", "目标" }),
        new(
            "macro.intent.wait_timing",
            "等待进场",
            "先不要交位置和技能，等资源、敌方失误或第三方接触。",
            new[] { "等待", "先别急", "不要轻举妄动", "进场时机" },
            "等待宏不是挂机，而是保留开团权；如果两家已接触，我方应靠近可介入位置，而不是离战场太远。",
            "映射到等待、压位、第三方利用和资源倒计时。",
            new[] { "宏意图", "等待", "时机", "压位" }),
        new(
            "macro.intent.kite_cooldown",
            "拉扯等冷却",
            "边退边打，把敌人骗到我方有利距离再反打。",
            new[] { "拉扯", "技能冷却", "骗过来打", "不要跑太远" },
            "拉扯宏的关键是不要退散；队伍要保持火力范围和撤退路线，等敌方深入或技能空掉。",
            "映射到诱追、反打窗口、技能威胁下降和队伍凝聚。",
            new[] { "宏意图", "拉扯", "冷却", "反打" }),
        new(
            "macro.intent.cover_touch",
            "前压掩护摸点",
            "主团用压力保护交互人员，让摸点成功而不是全团站点挨打。",
            new[] { "前压", "掩护摸点" },
            "掩护宏说明目标是拿分，不是无脑开团；前压要覆盖敌方打断线，同时避免过深。",
            "映射到可交互目标、敌方打断压力和主团压制位置。",
            new[] { "宏意图", "前压", "摸点", "掩护" }),
        new(
            "macro.intent.spread_anti_burst",
            "分散防爆发",
            "敌方可能要开团，降低群控、极限技和高台落点收益。",
            new[] { "分散", "分散站位", "对面可能要上" },
            "分散宏不是停止输出，而是横向展开保持火力，等敌方技能交完再收束集火。",
            "映射到敌方极限技、关键技能、高台落点和技能威胁风险。",
            new[] { "宏意图", "分散", "极限技", "防爆发" }),
        new(
            "macro.intent.attack_alliance",
            "指定阵营进攻",
            "把目标从玩家个体提升到阵营层面的战略锁定。",
            new[] { "黑涡", "双蛇", "恒辉", "红的", "黄的", "蓝的", "HW", "SS", "HH" },
            "阵营宏常用于三家博弈：打谁不是只看距离，而要看表分、战意、跳分、位置和谁会赢。",
            "映射到战略目标选择、假老三/真弱三、老一压力和比分流速。",
            new[] { "宏意图", "阵营", "三方", "战略目标" }),
        new(
            "macro.intent.feint_bait",
            "佯攻骗技能",
            "只制造进场压力，骗出防御、极限技或地形落点，不立刻硬吃。",
            new[] { "佯攻", "假打", "骗技能", "不要真打" },
            "佯攻宏要求队伍能上去就退；如果散人跟不住，系统应该降低真开团权重。",
            "映射到敌方高威胁技能、我方可接团不足、诱追和等待反打。",
            new[] { "宏意图", "佯攻", "骗技能", "反打" }),
        new(
            "macro.intent.single_target",
            "单点挨个杀",
            "没有稳定 AOE 窗口时用单体目标逐个造成减员。",
            new[] { "单点预备", "单体技能", "挨个杀", "不要浪费资源" },
            "单点宏常用于敌方分散、敌方防爆发或我方资源不齐；重点是不要乱换目标。",
            "映射到低血/被控/职业硬度目标评分和焦点稳定性。",
            new[] { "宏意图", "单点", "集火", "资源管理" }),
        new(
            "macro.intent.hit_and_run",
            "只打一波",
            "打完一套收益就撤，避免把位置和人头分送回去。",
            new[] { "只打一波", "打一套就溜", "打完就走" },
            "这类宏通常出现在收益窗口短、第三方靠近或撤退路线即将变差时。",
            "映射到击杀后收手、撤退路线、第三方夹击和资源刷新。",
            new[] { "宏意图", "打一波", "收手", "第三方" }),
        new(
            "macro.intent.reposition",
            "调整位置",
            "当前站位不服务下一步目标，需要换角度或回到主团线。",
            new[] { "调整位置", "位置不好", "跟随指挥" },
            "调整位置不是单纯后退；可能是避开夹角、贴近下一资源、离开门口或换高低差角度。",
            "映射到地形风险、撤退路线、目标距离和主团凝聚。",
            new[] { "宏意图", "位置", "地形", "转线" }),
        new(
            "macro.intent.limit_break_ready",
            "极限技就绪确认",
            "确认团队爆发资源是否足够支撑下一波开团。",
            new[] { "极限技好了吗", "好了扣1", "极限技" },
            "极限技宏代表指挥在等爆发阈值；系统不能读全队聊天反馈，但可以用可见职业/战斗记录估算就绪数量。",
            "映射到友方极限技威胁估算、倒计时爆发和等待开团窗口。",
            new[] { "宏意图", "极限技", "爆发", "准备" }),
        new(
            "macro.intent.wrap_behind",
            "绕后/侧翼",
            "换线从侧后方制造夹击，而不是正面硬撞。",
            new[] { "绕后", "绕一下", "侧翼" },
            "绕后宏要求主团和分队同步；孤立绕后会变成送人头。",
            "映射到侧翼路径、敌方主团朝向、主团凝聚和第三方风险。",
            new[] { "宏意图", "绕后", "夹击", "转线" }),
        new(
            "macro.intent.ambush_regroup",
            "埋伏/集合",
            "在指定点压低存在感，等待敌方或资源进入我方优势区。",
            new[] { "埋伏", "集合", "不要轻举妄动" },
            "埋伏宏强调提前站位与纪律；如果敌方不来或资源变化，要及时切换到压位而不是原地看戏。",
            "映射到等待、热区、地形优势、敌方转线预测。",
            new[] { "宏意图", "埋伏", "集合", "等待" }),
        new(
            "macro.intent.losing_all_in",
            "败势换击杀",
            "胜利路径极低时改为就地造击杀收益。",
            new[] { "战败", "大势已去", "就地开杀", "杀杀杀" },
            "这是情绪/收尾宏，不应常规触发；只有时间极少、比分无实际翻盘路径且附近有可杀目标时才可转为击杀优先。",
            "映射到极低翻盘率终局的击杀优先级；正常运营阶段禁止用它替代拿分。",
            new[] { "宏意图", "终局", "击杀", "低胜率" }),
    };

    private static readonly FrontlineBattleHighRewardSnapshot[] BattleHighRewards =
    {
        new("无", 6, 3),
        new("斗志昂扬 I", 8, 4),
        new("斗志昂扬 II", 10, 5),
        new("斗志昂扬 III", 12, 6),
        new("斗志昂扬 IV", 14, 7),
        new("斗志昂扬 V", 16, 8),
    };

    private static readonly FrontlineBattleHighTierSnapshot[] BattleHighTiers =
    {
        new(20, 39, "I", 10),
        new(40, 59, "II", 20),
        new(60, 79, "III", 30),
        new(80, 99, "IV", 40),
        new(100, 100, "V", 50),
    };

    private static readonly FrontlineLimitBreakRankRuleSnapshot[] LimitBreakRankRules =
    {
        new("第一名", -25, "极限技积蓄速度减速 25%；同分时按排名靠前/较慢规则计算，开局三家 0 分均视为第一名。"),
        new("第二名", 0, "极限技积蓄速度正常。"),
        new("第三名", 25, "极限技积蓄速度加速 25%。"),
    };

    private static readonly FrontlineJobAdjustmentSnapshot[] JobAdjustments =
    {
        CreateJobAdjustment(19, "骑士", 64500, 0, -50, 120, 0),
        CreateJobAdjustment(21, "战士", 66000, -10, -50, 90, 0),
        CreateJobAdjustment(32, "暗黑骑士", 64500, -20, -40, 105, 45),
        CreateJobAdjustment(37, "绝枪战士", 63000, 0, -55, 60, 15),
        CreateJobAdjustment(24, "白魔法师", 55500, -10, -25, 60, 15),
        CreateJobAdjustment(28, "学者", 55500, -10, -30, 90, 0),
        CreateJobAdjustment(33, "占星术士", 54000, -15, -25, 120, 15),
        CreateJobAdjustment(40, "贤者", 54000, 0, -35, 120, 0),
        CreateJobAdjustment(20, "武僧", 63000, 0, -50, 75, 0),
        CreateJobAdjustment(22, "龙骑士", 61500, -15, -50, 90, 15),
        CreateJobAdjustment(30, "忍者", 60000, 0, -45, 105, 15),
        CreateJobAdjustment(34, "武士", 61500, -10, -50, 120, 15),
        CreateJobAdjustment(39, "钐镰客", 61500, 0, -50, 75, 15),
        CreateJobAdjustment(41, "蝰蛇剑士", 61500, 0, -60, 90, 0),
        CreateJobAdjustment(23, "吟游诗人", 55500, 0, -30, 120, -15),
        CreateJobAdjustment(31, "机工士", 57000, 0, -30, 90, 0),
        CreateJobAdjustment(38, "舞者", 58500, 0, -35, 90, 15),
        CreateJobAdjustment(25, "黑魔法师", 54000, -5, -30, 60, 0),
        CreateJobAdjustment(27, "召唤师", 57000, -10, -30, 90, 15),
        CreateJobAdjustment(35, "赤魔法师", 58500, 0, -38, 90, 0),
        CreateJobAdjustment(42, "绘灵法师", 55500, -10, -30, 105, 0),
    };

    private static readonly FrontlineCrowdControlAdjustmentSnapshot[] CrowdControlAdjustments =
    {
        new("眩晕", -25),
        new("加重", -25),
        new("止步", -25),
        new("沉默", -25),
        new("睡眠", -25),
        new("冻结", -25),
    };

    private static readonly FrontlineDefenseInteractionSkillSnapshot[] DefenseInteractionSkills =
    {
        new("战士", 21, "原初的怒号", "解除防御", "群体", "可以群体解除他人防御。"),
        new("舞者", 38, "行列舞", "解除防御", "群体", "可以群体解除他人防御。"),
        new("钐镰客", 39, "暗夜游魂", "解除防御", "群体", "可以群体解除他人防御。"),
        new("武僧", 20, "陨石冲击", "解除防御", "单体", "可以单体解除他人防御。"),
        new("防护职业", null, "全力挥打", "解除防御", "单体", "可以单体解除他人防御。"),
        new("机工士", 31, "钻头", "无视防御减伤", "伤害", "可以无视防御减伤造成伤害。"),
    };

    private const string PvpActionsWikiSource = "https://ff14.huijiwiki.com/wiki/%E5%AF%B9%E6%88%98%E6%8A%80%E8%83%BD";

    private static readonly FrontlineKeySkillRuleSnapshot[] KeySkillRules =
    {
        CreateKeySkillRule("skill.common.guard", null, "通用", "防御 / Guard", BattlefieldKeySkillKind.Defensive, 30, 10f, false, false, false, false, "通用防御状态，敌方防御多时优先等破防或转火。", "通用", "防御"),
        CreateKeySkillRule("skill.common.purify", null, "通用", "净化 / Purify", BattlefieldKeySkillKind.Purify, 4, 14f, false, false, false, false, "解除控制并形成短暂抗控窗口；敌方净化后不适合继续交控制链。", "通用", "净化", "抗控"),
        CreateKeySkillRule("skill.common.recuperate", null, "通用", "自愈 / Recuperate", BattlefieldKeySkillKind.Defensive, 1, 8f, false, false, false, false, "通用自疗会抬高斩杀门槛，低血目标仍要看 MP 和是否被控。", "通用", "治疗", "MP"),

        CreateKeySkillRule("skill.role.tank.rampart", null, "防护职业", "铁壁 / Rampart", BattlefieldKeySkillKind.Defensive, 30, 13f, false, false, false, false, "防护职业常见减伤，目标身上出现时压低斩杀权重。", "角色技能", "坦克", "防御"),
        CreateKeySkillRule("skill.role.tank.full_swing", null, "防护职业", "全力挥打 / Full Swing", BattlefieldKeySkillKind.GuardBreak, 30, 19f, false, true, false, false, "防护职业单体破防工具，可处理关键防御目标。", "角色技能", "坦克", "破防"),
        CreateKeySkillRule("skill.role.melee.bloodbath", null, "近战职业", "浴血 / Bloodbath", BattlefieldKeySkillKind.Defensive, 30, 12f, false, false, false, false, "近战续航窗口，单点追击时要避免被反吸血拖住。", "角色技能", "近战", "续航"),

        CreateKeySkillRule("skill.pld.guardian", 19, "骑士", "守护 / Guardian", BattlefieldKeySkillKind.Defensive, 25, 14f, false, false, false, false, "骑士可保护关键目标并拖住点位。", "骑士", "保护", "防御"),
        CreateKeySkillRule("skill.pld.confiteor", 19, "骑士", "悔罪 / Confiteor", BattlefieldKeySkillKind.AreaPressure, 20, 17f, false, false, false, true, "骑士远程范围压制，适合目标区和卡口消耗。", "骑士", "范围"),
        CreateKeySkillRule("skill.pld.shield_bash", 19, "骑士", "盾牌猛击 / Shield Bash", BattlefieldKeySkillKind.CrowdControl, 20, 16f, true, false, false, false, "骑士单体控制可接集火或打断撤退。", "骑士", "控制"),

        CreateKeySkillRule("skill.drk.salted_earth", 32, "暗黑骑士", "腐秽大地 / Salted Earth", BattlefieldKeySkillKind.Engage, 30, 30f, true, false, false, true, "暗黑可把敌人拉进区域并制造集火入口。", "暗黑骑士", "开团", "拉人", "范围"),
        CreateKeySkillRule("skill.drk.salt_darkness", 32, "暗黑骑士", "腐秽黑暗 / Salt and Darkness", BattlefieldKeySkillKind.CrowdControl, 15, 23f, true, false, false, true, "暗黑区域后续控制，和队友爆发重叠时威胁很高。", "暗黑骑士", "控制", "拉人"),
        CreateKeySkillRule("skill.drk.tbn", 32, "暗黑骑士", "至黑之夜 / The Blackest Night", BattlefieldKeySkillKind.Defensive, 15, 13f, false, false, false, false, "暗黑护盾会拖低短时爆发收益。", "暗黑骑士", "防御"),

        CreateKeySkillRule("skill.war.primal_rend", 21, "战士", "原初的怒号 / Primal Rend", BattlefieldKeySkillKind.GuardBreak, 30, 28f, true, true, false, true, "战士可制造群体破防和推进窗口。", "战士", "破防", "控制", "开团"),
        CreateKeySkillRule("skill.war.blota", 21, "战士", "死斗 / Blota", BattlefieldKeySkillKind.Engage, 15, 21f, true, false, false, false, "战士拉人能把落单目标拖进己方火力。", "战士", "拉人", "集火"),
        CreateKeySkillRule("skill.war.bloodwhetting", 21, "战士", "原初的血气 / Bloodwhetting", BattlefieldKeySkillKind.Defensive, 20, 12f, false, false, false, false, "战士续航窗口，追击前要确认控制和爆发是否足够。", "战士", "续航"),

        CreateKeySkillRule("skill.gnb.double_down", 37, "绝枪战士", "倍攻 / Double Down", BattlefieldKeySkillKind.Burst, 20, 20f, false, false, false, true, "绝枪贴身范围爆发会压低前排血线。", "绝枪战士", "爆发", "范围"),
        CreateKeySkillRule("skill.gnb.nebula", 37, "绝枪战士", "星云 / Nebula", BattlefieldKeySkillKind.Defensive, 30, 12f, false, false, false, false, "绝枪减伤反打窗口，目标身上出现时降低追击收益。", "绝枪战士", "防御"),

        CreateKeySkillRule("skill.sch.biolysis", 28, "学者", "蛊毒法 / Biolysis", BattlefieldKeySkillKind.AreaPressure, 15, 15f, false, false, false, true, "学者持续压血会让撤退路线更危险。", "学者", "持续伤害"),
        CreateKeySkillRule("skill.sch.deployment", 28, "学者", "展开战术 / Deployment Tactics", BattlefieldKeySkillKind.Support, 15, 15f, false, false, false, true, "学者团队增益适合推点或反打。", "学者", "支援", "团队"),

        CreateKeySkillRule("skill.whm.miracle", 24, "白魔法师", "自然的奇迹 / Miracle of Nature", BattlefieldKeySkillKind.CrowdControl, 25, 23f, true, false, false, false, "白魔单点变形能稳定接集火。", "白魔法师", "控制", "集火"),
        CreateKeySkillRule("skill.whm.misery", 24, "白魔法师", "苦难之心 / Afflatus Misery", BattlefieldKeySkillKind.Burst, 15, 18f, false, false, false, true, "白魔范围爆发常跟控制窗口重叠。", "白魔法师", "爆发", "范围"),

        CreateKeySkillRule("skill.ast.gravity", 33, "占星术士", "重力 / Gravity II", BattlefieldKeySkillKind.CrowdControl, 15, 17f, true, false, false, true, "占星可限制转点和撤退节奏。", "占星术士", "控制", "减速"),
        CreateKeySkillRule("skill.ast.macrocosmos", 33, "占星术士", "大宇宙 / Macrocosmos", BattlefieldKeySkillKind.Support, 30, 17f, false, false, false, true, "占星群体回复/反打窗口会改变团战血线。", "占星术士", "支援", "回复"),

        CreateKeySkillRule("skill.sge.pneuma", 40, "贤者", "魂灵风息 / Pneuma", BattlefieldKeySkillKind.Support, 25, 17f, false, false, false, true, "贤者可用直线压制和回复支撑推进。", "贤者", "支援", "范围"),
        CreateKeySkillRule("skill.sge.icarus", 40, "贤者", "神翼 / Icarus", BattlefieldKeySkillKind.Engage, 15, 14f, false, false, false, false, "贤者位移进入射程时要注意后续压制。", "贤者", "位移"),

        CreateKeySkillRule("skill.mnk.rising_phoenix", 20, "武僧", "凤凰舞 / Rising Phoenix", BattlefieldKeySkillKind.GuardBreak, 20, 21f, false, true, true, false, "武僧可处理关键防御目标并接单点爆发。", "武僧", "破防", "爆发"),
        CreateKeySkillRule("skill.mnk.enlightenment", 20, "武僧", "震脚/斗气爆发", BattlefieldKeySkillKind.Burst, 20, 18f, false, false, true, false, "武僧贴身爆发对落单目标威胁高。", "武僧", "爆发", "斩杀"),

        CreateKeySkillRule("skill.drg.wyrmwind", 22, "龙骑士", "天龙点睛 / Wyrmwind Thrust", BattlefieldKeySkillKind.Burst, 20, 20f, false, false, false, true, "龙骑士对直线和密集人群有高爆发压力。", "龙骑士", "爆发", "范围"),
        CreateKeySkillRule("skill.drg.horrid_roar", 22, "龙骑士", "恐惧咆哮 / Horrid Roar", BattlefieldKeySkillKind.CrowdControl, 25, 16f, true, false, false, true, "龙骑士范围压制可配合跳入爆发。", "龙骑士", "控制", "范围"),

        CreateKeySkillRule("skill.nin.assassinate", 30, "忍者", "断绝 / Assassinate", BattlefieldKeySkillKind.Execute, 15, 27f, false, false, true, false, "忍者对低血和高战意目标斩杀威胁高。", "忍者", "斩杀"),
        CreateKeySkillRule("skill.nin.bunshin", 30, "忍者", "分身之术 / Bunshin", BattlefieldKeySkillKind.Burst, 30, 18f, false, false, true, false, "忍者爆发前摇，看到后要提高残血风险。", "忍者", "爆发"),

        CreateKeySkillRule("skill.sam.chiten", 34, "武士", "必杀剑·地天 / Hissatsu: Chiten", BattlefieldKeySkillKind.Defensive, 30, 18f, false, false, true, false, "武士反打窗口，盲目集火容易被反杀。", "武士", "反打", "防御"),
        CreateKeySkillRule("skill.sam.ogi", 34, "武士", "奥义斩浪 / Ogi Namikiri", BattlefieldKeySkillKind.Burst, 20, 22f, false, false, true, true, "武士直线爆发适合收割密集或被控目标。", "武士", "爆发", "斩杀"),

        CreateKeySkillRule("skill.rpr.grim_swathe", 39, "钐镰客", "束缚挥割 / Grim Swathe", BattlefieldKeySkillKind.CrowdControl, 30, 26f, true, true, false, true, "钐镰客可制造群体恐慌/破防窗口。", "钐镰客", "控制", "破防"),
        CreateKeySkillRule("skill.rpr.arcane_crest", 39, "钐镰客", "神秘纹 / Arcane Crest", BattlefieldKeySkillKind.Defensive, 20, 12f, false, false, false, false, "钐镰客护盾可支撑进场和反打。", "钐镰客", "防御"),

        CreateKeySkillRule("skill.vpr.uncoiled_fury", 41, "蝰蛇剑士", "飞蛇之牙 / Uncoiled Fury", BattlefieldKeySkillKind.Burst, 20, 21f, false, false, true, false, "蝰蛇剑士贴身爆发对落单和残血目标威胁高。", "蝰蛇剑士", "爆发", "斩杀"),
        CreateKeySkillRule("skill.vpr.slither", 41, "蝰蛇剑士", "蛇行 / Slither", BattlefieldKeySkillKind.Engage, 15, 16f, false, false, false, false, "蝰蛇位移接近时要预判后续爆发。", "蝰蛇剑士", "位移", "爆发"),

        CreateKeySkillRule("skill.brd.silent_nocturne", 23, "吟游诗人", "沉默的夜曲 / Silent Nocturne", BattlefieldKeySkillKind.CrowdControl, 20, 16f, true, false, false, false, "吟游诗人可远程沉默关键读条和撤退。", "吟游诗人", "沉默", "控制"),
        CreateKeySkillRule("skill.brd.apex_arrow", 23, "吟游诗人", "绝峰箭 / Apex Arrow", BattlefieldKeySkillKind.AreaPressure, 15, 16f, false, false, false, true, "吟游直线压制适合卡口和目标区。", "吟游诗人", "范围"),

        CreateKeySkillRule("skill.mch.drill", 31, "机工士", "钻头 / Drill", BattlefieldKeySkillKind.DefensePierce, 20, 25f, false, true, true, false, "机工士钻头可无视防御减伤打关键残血。", "机工士", "穿防", "斩杀"),
        CreateKeySkillRule("skill.mch.chainsaw", 31, "机工士", "回转飞锯 / Chain Saw", BattlefieldKeySkillKind.Burst, 20, 20f, false, false, true, true, "机工士远程爆发会制造残血斩杀窗口。", "机工士", "爆发", "范围"),

        CreateKeySkillRule("skill.dnc.honing_dance", 38, "舞者", "行列舞 / Honing Dance", BattlefieldKeySkillKind.GuardBreak, 30, 27f, true, true, false, true, "舞者可用群体控制和拆防打开团战。", "舞者", "控制", "破防"),
        CreateKeySkillRule("skill.dnc.curing_waltz", 38, "舞者", "治疗之华尔兹 / Curing Waltz", BattlefieldKeySkillKind.Support, 20, 13f, false, false, false, true, "舞者群体回复会抬高近战反打韧性。", "舞者", "支援", "回复"),

        CreateKeySkillRule("skill.blm.night_wing", 25, "黑魔法师", "夜翼 / Night Wing", BattlefieldKeySkillKind.CrowdControl, 25, 20f, true, false, false, true, "黑魔睡眠/控制对密集目标区威胁高。", "黑魔法师", "睡眠", "控制"),
        CreateKeySkillRule("skill.blm.burst", 25, "黑魔法师", "爆炎 / Burst", BattlefieldKeySkillKind.AreaPressure, 20, 18f, false, false, false, true, "黑魔范围压制适合逼退卡口和点内人群。", "黑魔法师", "范围"),

        CreateKeySkillRule("skill.smn.slipstream", 27, "召唤师", "螺旋气流 / Slipstream", BattlefieldKeySkillKind.AreaPressure, 20, 19f, true, false, false, true, "召唤师区域压制会限制目标区走位。", "召唤师", "范围", "控制"),
        CreateKeySkillRule("skill.smn.mountain_buster", 27, "召唤师", "山崩 / Mountain Buster", BattlefieldKeySkillKind.Burst, 20, 19f, false, false, false, true, "召唤师范围爆发适合压密集目标。", "召唤师", "爆发", "范围"),

        CreateKeySkillRule("skill.rdm.resolution", 35, "赤魔法师", "决断 / Resolution", BattlefieldKeySkillKind.CrowdControl, 20, 19f, true, false, false, true, "赤魔直线控制可衔接中距离爆发。", "赤魔法师", "控制", "范围"),
        CreateKeySkillRule("skill.rdm.corps", 35, "赤魔法师", "短兵相接 / Corps-a-corps", BattlefieldKeySkillKind.Engage, 15, 15f, false, false, true, false, "赤魔突进后通常接连击爆发。", "赤魔法师", "位移", "爆发"),

        CreateKeySkillRule("skill.pic.star_prism", 42, "绘灵法师", "星空棱镜 / Star Prism", BattlefieldKeySkillKind.AreaPressure, 25, 20f, false, false, false, true, "绘灵法师远程范围压制会影响目标区站位。", "绘灵法师", "范围", "爆发"),
        CreateKeySkillRule("skill.pic.motif", 42, "绘灵法师", "动物/武器构想", BattlefieldKeySkillKind.Support, 25, 16f, false, false, false, true, "绘灵法师构想技能会形成短时爆发或支援窗口。", "绘灵法师", "支援", "爆发"),
    };

    private static readonly FrontlineMapKnowledgeSnapshot BorderlandRuinsSecureKnowledge = new()
    {
        MapType = FrontlineMapType.BorderlandRuinsSecure,
        TerritoryTypeIds = new[] { 376u, 1273u },
        Name = "周边遗迹群（阵地战）",
        RuleSetName = "阵地战",
        VictoryScore = 2400,
        PrimaryObjective = "占领并维持更多据点，同时围绕怪物刷新和击倒收益滚动扩大分差。",
        RankingRule = "最快达到 2400 战术值的阵营获胜；未到 2400 时按战术值高低排名。",
        Rules = new[]
        {
            new FrontlineKnowledgeRuleSnapshot(
                "map.secure.victory_score",
                "胜利目标 2400 战术值",
                "周边遗迹群（阵地战）中，最快累积到 2400 战术值的团队胜出；比分高低决定当前排名。",
                new[] { "周边遗迹群", "胜利条件", "比分" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.secure.key_locations",
                "9 个据点是主线收益",
                "地图存在 9 处据点，在旗帜周围停留可占领；占领据点越多，每 3 秒获得的战术值越高。",
                new[] { "周边遗迹群", "据点", "占领" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.secure.sheer_will",
                "据点圈内坚定不移",
                "据点圈内玩家会获得坚定不移状态，攻击造成的伤害提高 50%；据点内交战价值和风险都更高。",
                new[] { "周边遗迹群", "据点", "坚定不移", "伤害" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.secure.weaponry_warning",
                "怪物刷新前 30 秒有地图预告",
                "怪物出现前 30 秒，2D 地图会出现预告图标；专用 UI 可显示下一次怪物类型和出现时间、当前怪物类型和剩余时间。",
                new[] { "周边遗迹群", "怪物", "地图事件", "预告" }),
        },
        BaseCaptureScores = new[]
        {
            new FrontlineBaseCaptureScoreSnapshot(1, 2, 3),
            new FrontlineBaseCaptureScoreSnapshot(2, 4, 3),
            new FrontlineBaseCaptureScoreSnapshot(3, 6, 3),
            new FrontlineBaseCaptureScoreSnapshot(4, 10, 3),
            new FrontlineBaseCaptureScoreSnapshot(5, 12, 3),
            new FrontlineBaseCaptureScoreSnapshot(6, 14, 3),
            new FrontlineBaseCaptureScoreSnapshot(7, 18, 3),
            new FrontlineBaseCaptureScoreSnapshot(8, 20, 3),
            new FrontlineBaseCaptureScoreSnapshot(9, 22, 3),
        },
        ScoreSources = new[]
        {
            new FrontlineScoreSourceSnapshot(
                "占领据点",
                null,
                null,
                "占领据点后每 3 秒按据点数量获得战术值，4 个和 7 个据点是收益跳变点。",
                new[] { "据点", "持续收益" }),
            new FrontlineScoreSourceSnapshot(
                "击倒敌方玩家",
                10,
                -10,
                "打倒其他阵营玩家后己方 +10 战术值，对方 -10 战术值。",
                new[] { "击倒", "玩家", "分差" }),
            new FrontlineScoreSourceSnapshot(
                "截击无人机",
                10,
                null,
                "击破普通截击无人机按伤害贡献分配 10 战术值；造成最多伤害的小队获得 4 战意。",
                new[] { "怪物", "无人机", "战意" }),
            new FrontlineScoreSourceSnapshot(
                "截击无人机α",
                30,
                null,
                "金色截击无人机α出现概率较低，当前按 Patch 7.25 资料记录为 30 战术值，并给予最高贡献小队更高战意收益。",
                new[] { "怪物", "无人机α", "高价值" }),
            new FrontlineScoreSourceSnapshot(
                "截击指挥系统",
                24,
                null,
                "接住截击指挥系统召唤的塔可获得共计 24 战术值和 4 战意；多阵营同时踩塔时战术值会平分。",
                new[] { "怪物", "指挥系统", "踩塔", "战意" }),
            new FrontlineScoreSourceSnapshot(
                "截击系统",
                150,
                null,
                "截击系统每次血量归零按三方伤害比例分配 150 战术值，共 6 次、总计 900 战术值。",
                new[] { "怪物", "截击系统", "高价值", "伤害贡献" }),
        },
        TimedSpawns = new[]
        {
            new FrontlineTimedSpawnRuleSnapshot(
                "map.secure.spawn.first",
                "首批怪物",
                40,
                30,
                0,
                "开场约 40 秒刷新第一批敌人；刷新前 30 秒地图和专用 UI 会给出预告。",
                new[] { "刷新", "开场", "预告" }),
            new FrontlineTimedSpawnRuleSnapshot(
                "map.secure.spawn.drone",
                "截击无人机",
                null,
                30,
                50,
                "截击无人机出现后会持续攻击附近玩家，持续约 50 秒后自毁。",
                new[] { "刷新", "无人机", "50秒" }),
            new FrontlineTimedSpawnRuleSnapshot(
                "map.secure.spawn.command_system",
                "截击指挥系统",
                null,
                30,
                70,
                "截击指挥系统持续约 70 秒，不能被攻击，会在六边形格子召唤塔并造成区域压力。",
                new[] { "刷新", "指挥系统", "70秒", "踩塔" }),
            new FrontlineTimedSpawnRuleSnapshot(
                "map.secure.spawn.node",
                "截击系统",
                null,
                30,
                120,
                "截击系统持续约 120 秒，每次血量归零都会按伤害贡献给分，是该地图最高价值怪物。",
                new[] { "刷新", "截击系统", "120秒", "高价值" }),
        },
        ObjectiveRules = new[]
        {
            new FrontlineMapObjectiveRuleSnapshot(
                "map.secure.objective.flags",
                "据点旗帜",
                "控制点",
                null,
                null,
                null,
                "占领和维持据点是稳定得分主线；敌方进入己方旗帜范围会让据点变中立或争夺。",
                new[] { "据点", "主目标", "占领" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.secure.objective.drone",
                "截击无人机",
                "怪物",
                10,
                4,
                50,
                "按伤害贡献分配 10 战术值，最高贡献小队获得 4 战意。",
                new[] { "怪物", "无人机", "战意" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.secure.objective.drone_alpha",
                "截击无人机α",
                "怪物",
                30,
                8,
                50,
                "稀有金色无人机，价值高于普通无人机；当前按 30 战术值和 8 战意记录。",
                new[] { "怪物", "无人机α", "高价值", "战意" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.secure.objective.command_system",
                "截击指挥系统",
                "机制怪",
                24,
                4,
                70,
                "本体不能攻击；踩塔可得 24 战术值和 4 战意，塔内多阵营会平分；踩塔约受 1000 原始爆炸伤害，无人踩塔会造成约 30000 大范围爆炸伤害。",
                new[] { "怪物", "指挥系统", "踩塔", "AOE" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.secure.objective.node",
                "截击系统",
                "Boss",
                900,
                null,
                120,
                "每次血量归零分配 150 战术值，共计 900；会释放对地炮击、集中炮击、精密炮击和物体130等技能。",
                new[] { "怪物", "截击系统", "Boss", "高价值" }),
        },
        TeleportRules = new[]
        {
            new FrontlineTeleportRuleSnapshot(
                "传送装置",
                30,
                3,
                "传送装置在怪物出现前 30 秒开始运转，站上去会传送到高台并获得 3 秒无敌；怪物结束后停止运转，传送前骑乘会解除坐骑。"),
        },
        DecisionHints = new[]
        {
            new FrontlineDecisionHintSnapshot(
                "hint.secure.base_count_breakpoints",
                "我方据点数接近 4 个或 7 个",
                "优先评估能否抢到下一座据点，因为 4 个和 7 个据点会让每 3 秒收益明显跳变。",
                "该图据点收益不是线性递增：3 到 4 从 6 跳到 10，6 到 7 从 14 跳到 18。",
                new[] { "周边遗迹群", "据点", "收益跳变" }),
            new FrontlineDecisionHintSnapshot(
                "hint.secure.weaponry_prepare",
                "地图出现怪物预告或传送装置启动",
                "提前 20-30 秒判断我方人数、敌方主团方向和当前据点收益，决定转高台、卡传送点或放弃换据点。",
                "怪物刷新前 30 秒有预告，传送装置也会启动；提前站位比刷新后临时赶路更重要。",
                new[] { "周边遗迹群", "怪物", "转点", "传送" }),
            new FrontlineDecisionHintSnapshot(
                "hint.secure.node_value",
                "截击系统刷新且两家敌方正在靠近",
                "不要只看 Boss 血量，要按伤害贡献、我方存活人数和被第三方夹击风险决定是否全团投入。",
                "截击系统总价值 900 分，但收益按伤害贡献拆分；被两家夹在高台会把高价值目标变成送分点。",
                new[] { "周边遗迹群", "截击系统", "贡献", "夹击风险" }),
            new FrontlineDecisionHintSnapshot(
                "hint.secure.command_tower",
                "截击指挥系统塔点出现",
                "优先让安全位置的人踩塔，敌我混踩时收益会被平分；无人踩塔会触发大范围高伤害，要把风险纳入撤退判断。",
                "踩塔能换分和战意，但塔内多阵营会分收益，空塔则释放大范围爆炸。",
                new[] { "周边遗迹群", "指挥系统", "踩塔", "风险" }),
            new FrontlineDecisionHintSnapshot(
                "hint.secure.kill_vs_base",
                "局部可以追击残血敌人但会放空据点",
                "优先计算追击 +10/-10 的分差是否大于丢据点后的持续亏分；没有把握击杀时应回旗或转下一个点。",
                "击倒是一次性收益，据点是持续收益；阵地战指挥要避免为低概率追击牺牲稳定占点。",
                new[] { "周边遗迹群", "击倒", "据点", "机会成本" }),
        },
        SummaryText = "周边遗迹群（阵地战）：2400 分胜利，9 据点持续给分，4/7 据点是收益跳变点；怪物前 30 秒预告，截击系统总价值最高。"
    };

    private static readonly FrontlineMapKnowledgeSnapshot SealRockSeizeKnowledge = new()
    {
        MapType = FrontlineMapType.SealRock,
        TerritoryTypeIds = new[] { 431u },
        Name = "尘封秘岩（争夺战）",
        RuleSetName = "争夺战",
        VictoryScore = 700,
        PrimaryObjective = "争夺随机启动的亚拉戈石文，通过持续占领高等级石文和击倒收益积累情报值。",
        RankingRule = "最快达到 700 情报值的阵营获胜；未到 700 时按情报值高低排名。",
        Rules = new[]
        {
            new FrontlineKnowledgeRuleSnapshot(
                "map.seal_rock.victory_score",
                "胜利目标 700 情报值",
                "尘封秘岩（争夺战）中，最快累积到 700 情报值的团队获胜；比分高低决定当前排名。",
                new[] { "尘封秘岩", "胜利条件", "比分" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.seal_rock.tomelith.core",
                "亚拉戈石文是主线目标",
                "场上设置多个拥有情报值的亚拉戈石文；只有启动状态的石文能被占领，占领后会持续提供情报值。",
                new[] { "尘封秘岩", "亚拉戈石文", "主目标" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.seal_rock.tomelith.capture",
                "接触启动石文改变占领状态",
                "玩家接触启动中的亚拉戈石文可进行占领：中立石文会变为己方占领，敌方占领石文会先变为中立。",
                new[] { "尘封秘岩", "占领", "中立" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.seal_rock.tomelith_lockout",
                "石文变更后约 3 秒锁定",
                "亚拉戈石文占领状态变化后，约 3 秒内无法再次接触改变状态；指挥判断抢点时要计入这个短暂锁定窗口。",
                new[] { "尘封秘岩", "占领", "锁定窗口" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.seal_rock.rank_probability",
                "高等级石文随时间更容易启动",
                "亚拉戈石文启动时会随机为 S、A、B 三种等级，等级越高概率越低但总情报值更高；随着比赛时间推进，高等级石文更可能启动。",
                new[] { "尘封秘岩", "等级", "时间阶段" }),
        },
        ObjectiveRankScores = new[]
        {
            new FrontlineObjectiveRankScoreSnapshot("S", 160, 4, 3, 40, 120),
            new FrontlineObjectiveRankScoreSnapshot("A", 120, 3, 3, 40, 120),
            new FrontlineObjectiveRankScoreSnapshot("B", 80, 2, 3, 40, 120),
        },
        ScoreSources = new[]
        {
            new FrontlineScoreSourceSnapshot(
                "占领亚拉戈石文",
                null,
                null,
                "占领启动状态的亚拉戈石文后持续获得情报值；每 3 秒跳一次，共 40 跳，持续 2 分钟。",
                new[] { "亚拉戈石文", "持续收益" }),
            new FrontlineScoreSourceSnapshot(
                "击倒敌方玩家",
                5,
                -5,
                "打倒其他阵营玩家后己方 +5 情报值，对方 -5 情报值。",
                new[] { "击倒", "玩家", "分差" }),
        },
        TimedSpawns = new[]
        {
            new FrontlineTimedSpawnRuleSnapshot(
                "map.seal_rock.spawn.first",
                "首批亚拉戈石文",
                30,
                0,
                120,
                "比赛开始 30 秒后，也就是剩余 19:30 时，场上会启动第一批 4 个亚拉戈石文。",
                new[] { "启动", "开场", "4个" }),
            new FrontlineTimedSpawnRuleSnapshot(
                "map.seal_rock.spawn.next",
                "后续亚拉戈石文",
                null,
                0,
                120,
                "亚拉戈石文情报值耗尽并停止后，约 15 秒会随机启动下一个停止状态的石文。",
                new[] { "启动", "循环", "15秒" }),
        },
        ObjectiveRules = new[]
        {
            new FrontlineMapObjectiveRuleSnapshot(
                "map.seal_rock.objective.tomelith",
                "亚拉戈石文",
                "控制点",
                null,
                null,
                120,
                "启动状态下可占领，停止状态无法获取情报值；占领状态变化后约 3 秒不能再次接触。",
                new[] { "亚拉戈石文", "控制点", "占领" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.seal_rock.objective.rank_s",
                "S 级亚拉戈石文",
                "高价值控制点",
                160,
                null,
                120,
                "总计 160 情报值，每 3 秒 4 分，共 40 跳；是最值得提前集结争夺的目标。",
                new[] { "亚拉戈石文", "S级", "高价值" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.seal_rock.objective.rank_a",
                "A 级亚拉戈石文",
                "中高价值控制点",
                120,
                null,
                120,
                "总计 120 情报值，每 3 秒 3 分，共 40 跳；10 分钟后刷新至少为 A 级。",
                new[] { "亚拉戈石文", "A级", "中高价值" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.seal_rock.objective.rank_b",
                "B 级亚拉戈石文",
                "基础控制点",
                80,
                null,
                120,
                "总计 80 情报值，每 3 秒 2 分，共 40 跳；前期常见，但后期价值会被 A/S 级目标拉开。",
                new[] { "亚拉戈石文", "B级", "基础收益" }),
        },
        PhaseRules = new[]
        {
            new FrontlineMapPhaseRuleSnapshot(
                "前 10 分钟",
                0,
                600,
                4,
                "B",
                "比赛前 10 分钟场上最多同时存在 4 个启动石文，S/A/B 均可能出现。",
                new[] { "阶段", "前期", "4个", "SAB" }),
            new FrontlineMapPhaseRuleSnapshot(
                "10 分钟后",
                600,
                null,
                3,
                "A",
                "比赛开始 10 分钟后，场上最多同时存在 3 个启动石文，后续刷新至少为 A 级。",
                new[] { "阶段", "后期", "3个", "至少A级" }),
        },
        DecisionHints = new[]
        {
            new FrontlineDecisionHintSnapshot(
                "hint.seal_rock.rank_priority",
                "多个石文同时启动且等级不同",
                "优先围绕 S 级和后期 A 级组织主团，低等级石文可以交给小队或用作牵制，避免主团为 B 点付出过高伤亡。",
                "S/A/B 总分分别是 160/120/80，且每 3 秒持续跳分；高等级石文的长期收益明显更高。",
                new[] { "尘封秘岩", "石文等级", "主团调度" }),
            new FrontlineDecisionHintSnapshot(
                "hint.seal_rock.neutralize_enemy_node",
                "敌方正在独占高等级石文",
                "如果无法立刻抢成己方，也应评估先触碰变中立来切断敌方跳分，再等待 3 秒锁定结束后组织二次占领。",
                "敌方占领石文被接触后会先变中立，占领变化后约 3 秒无法再次接触；中立化本身就能阻断持续收益。",
                new[] { "尘封秘岩", "中立化", "断分" }),
            new FrontlineDecisionHintSnapshot(
                "hint.seal_rock.after_ten_minutes",
                "比赛进入 10 分钟后",
                "把指挥重心从铺开抢数量转为抢高等级质量；此后启动数量减少到 3 个，且刷新至少为 A 级，团战价值更集中。",
                "10 分钟后同时启动上限从 4 个降到 3 个，且不再刷新 B 级石文。",
                new[] { "尘封秘岩", "时间阶段", "后期" }),
            new FrontlineDecisionHintSnapshot(
                "hint.seal_rock.kill_vs_tomelith",
                "局部追击会导致石文无人占领",
                "击倒收益只有 +5/-5，除非能稳定击杀或阻止敌方占点，否则应优先保证高等级石文持续跳分。",
                "一个 A 级石文每跳 3 分、总 120；为追一个低概率击杀而丢持续跳分通常不划算。",
                new[] { "尘封秘岩", "击倒", "机会成本" }),
            new FrontlineDecisionHintSnapshot(
                "hint.seal_rock.spawn_tempo",
                "石文停止后 15 秒窗口",
                "在石文即将耗尽或刚停止时，提前观察敌方大团方向和我方分队位置，准备抢下一轮随机启动点。",
                "石文停止约 15 秒后会启动下一个石文，提前转线能吃到第一波占领主动权。",
                new[] { "尘封秘岩", "刷新节奏", "转点" }),
        },
        SummaryText = "尘封秘岩（争夺战）：700 分胜利，S/A/B 石文总分 160/120/80，每 3 秒跳分 40 次；19:30 首刷 4 个，10 分钟后最多 3 个且至少 A 级。"
    };

    private static readonly FrontlineMapKnowledgeSnapshot OnsalHakairDanshigNaadamKnowledge = new()
    {
        MapType = FrontlineMapType.OnsalHakair,
        TerritoryTypeIds = new[] { 888u },
        Name = "昂萨哈凯尔（竞争战）",
        RuleSetName = "竞争战",
        VictoryScore = 1400,
        PrimaryObjective = "抢先契约随机出现的无垢的大地；目标一旦契约不可被敌方夺回，因此摸点和打断摸点是核心。",
        RankingRule = "最快达到 1400 战略值的阵营获胜；未到 1400 时按战略值高低排名。",
        Rules = new[]
        {
            new FrontlineKnowledgeRuleSnapshot(
                "map.onsal.victory_score",
                "胜利目标 1400 战略值",
                "昂萨哈凯尔（竞争战）中，最快累积到 1400 战略值的团队获胜；比分高低决定当前排名。",
                new[] { "昂萨哈凯尔", "胜利条件", "比分" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.onsal.ovoos.core",
                "无垢的大地是不可夺回资源",
                "无垢的大地类似尘封秘岩的分数资源，但一旦被契约就不可被敌方夺回；因此提前站位和摸点成功率极其关键。",
                new[] { "昂萨哈凯尔", "无垢的大地", "不可夺回", "摸点" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.onsal.ovoos.states",
                "无垢的大地固定四阶段",
                "无垢的大地必定按待机、预告、可契约、契约状态顺序变化；预告阶段会显示距离可契约的剩余时间。",
                new[] { "昂萨哈凯尔", "状态", "预告", "可契约" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.onsal.contract.interruptible",
                "契约需要 6 秒且会被攻击打断",
                "无垢的大地进入可契约后需要手动右键并持续约 6 秒完成契约；签订过程中受到敌方攻击会中断。",
                new[] { "昂萨哈凯尔", "契约", "打断", "6秒" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.onsal.center_rank_probability",
                "中心区更容易刷高等级",
                "越接近原野中心地区的无垢的大地越容易出现高等级；中心点位 01/11/12/13 的高等级概率高于其他点位，且 01 最少为 A 级。",
                new[] { "昂萨哈凯尔", "中心区", "等级", "点位" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.onsal.aoe_denial",
                "范围攻击能有效阻止敌方摸点",
                "想要阻止敌人契约无垢的大地，在目标附近持续使用范围攻击非常有效，因为受到攻击会中断 6 秒契约动作。",
                new[] { "昂萨哈凯尔", "范围攻击", "打断", "防守" }),
        },
        ObjectiveRankScores = new[]
        {
            new FrontlineObjectiveRankScoreSnapshot("S", 200, 20, 3, 10, 30),
            new FrontlineObjectiveRankScoreSnapshot("A", 100, 10, 3, 10, 30),
            new FrontlineObjectiveRankScoreSnapshot("B", 50, 5, 3, 10, 30),
        },
        ScoreSources = new[]
        {
            new FrontlineScoreSourceSnapshot(
                "契约无垢的大地",
                null,
                null,
                "契约可契约状态的无垢的大地后持续获得战略值；每 3 秒跳一次，共 10 跳，持续 30 秒。",
                new[] { "无垢的大地", "持续收益", "契约" }),
            new FrontlineScoreSourceSnapshot(
                "击倒敌方玩家",
                8,
                -8,
                "打倒其他阵营玩家后己方 +8 战略值，对方 -8 战略值。",
                new[] { "击倒", "玩家", "分差" }),
        },
        TimedSpawns = new[]
        {
            new FrontlineTimedSpawnRuleSnapshot(
                "map.onsal.spawn.warning",
                "无垢的大地预告",
                null,
                30,
                30,
                "场地会提前 30 秒预告即将可契约的无垢的大地位置；契约后跳分 30 秒，然后刷新到其他待机点位，不会原地刷新。",
                new[] { "刷新", "预告", "30秒", "不原地刷新" }),
            new FrontlineTimedSpawnRuleSnapshot(
                "map.onsal.spawn.contract",
                "可契约窗口",
                null,
                0,
                30,
                "倒计时结束后显示无垢的大地并进入可契约状态，玩家需要约 6 秒完成契约；成功契约后进入 30 秒跳分。",
                new[] { "可契约", "契约", "6秒", "30秒" }),
        },
        ObjectiveRules = new[]
        {
            new FrontlineMapObjectiveRuleSnapshot(
                "map.onsal.objective.ovoos",
                "无垢的大地",
                "契约点",
                null,
                null,
                30,
                "预告 30 秒后进入可契约状态，手动契约约 6 秒；成功契约后不可被敌方夺回，并持续 30 秒跳分。",
                new[] { "无垢的大地", "契约点", "不可夺回" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.onsal.objective.rank_s",
                "S 级无垢的大地",
                "高价值契约点",
                200,
                null,
                30,
                "总计 200 战略值，每 3 秒 20 分，共 10 跳；后期尤其应围绕 S 点组织主团。",
                new[] { "无垢的大地", "S级", "高价值" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.onsal.objective.rank_a",
                "A 级无垢的大地",
                "中高价值契约点",
                100,
                null,
                30,
                "总计 100 战略值，每 3 秒 10 分，共 10 跳；中心点和中后期常见，是主团主要争夺对象。",
                new[] { "无垢的大地", "A级", "中高价值" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.onsal.objective.rank_b",
                "B 级无垢的大地",
                "基础契约点",
                50,
                null,
                30,
                "总计 50 战略值，每 3 秒 5 分，共 10 跳；前期数量多，适合小队快速摸点或牵制。",
                new[] { "无垢的大地", "B级", "基础收益" }),
        },
        PhaseRules = new[]
        {
            new FrontlineMapPhaseRuleSnapshot(
                "19:55-15:00",
                5,
                300,
                6,
                "B",
                "启动上限 6 个，大部分为 B 级，小部分为 A 级；此阶段只有中心点 01/11/12/13 会出现 A 级。",
                new[] { "阶段", "前期", "6个", "B多A少" }),
            new FrontlineMapPhaseRuleSnapshot(
                "15:00-10:00",
                300,
                600,
                4,
                "B",
                "启动上限 4 个，A/B 均有，S 级存在但罕见；15:00-5:00 期间只有中心点会出现 S 级。",
                new[] { "阶段", "中期", "4个", "S罕见" }),
            new FrontlineMapPhaseRuleSnapshot(
                "10:00-5:00",
                600,
                900,
                3,
                "A",
                "启动上限 3 个，大部分为 A 级，小部分为 S 级；S 级仍主要限定在中心点 01/11/12/13。",
                new[] { "阶段", "后期", "3个", "A多S少" }),
            new FrontlineMapPhaseRuleSnapshot(
                "5:00-0:00",
                900,
                1200,
                2,
                "S",
                "启动上限 2 个，只有 S 级；最后阶段目标少且价值极高，主团决策要更集中。",
                new[] { "阶段", "决胜", "2个", "只有S" }),
        },
        LocationRules = new[]
        {
            new FrontlineMapLocationRuleSnapshot(
                "中心高等级点位",
                new[] { "01", "11", "12", "13" },
                null,
                null,
                string.Empty,
                "中心地区 01/11/12/13 出现高等级无垢的大地的概率高于其他点位。",
                new[] { "中心区", "点位", "概率" }),
            new FrontlineMapLocationRuleSnapshot(
                "01 点最低 A 级",
                new[] { "01" },
                null,
                null,
                "A",
                "01 出现的无垢的大地最少为 A 级，是需要重点提前观察和抢占的位置。",
                new[] { "中心区", "01", "至少A级" }),
            new FrontlineMapLocationRuleSnapshot(
                "前期 A 级限定区",
                new[] { "01", "11", "12", "13" },
                5,
                300,
                "A",
                "19:55-15:00 期间，只有 01/11/12/13 会出现 A 级无垢的大地。",
                new[] { "中心区", "A级", "前期" }),
            new FrontlineMapLocationRuleSnapshot(
                "中后期 S 级限定区",
                new[] { "01", "11", "12", "13" },
                300,
                900,
                "S",
                "15:00-5:00 期间，只有 01/11/12/13 会出现 S 级无垢的大地。",
                new[] { "中心区", "S级", "中后期" }),
        },
        DecisionHints = new[]
        {
            new FrontlineDecisionHintSnapshot(
                "hint.onsal.contract_first",
                "无垢的大地进入可契约倒计时末段",
                "优先确保有人能安全完成 6 秒契约；一旦摸到点就不可被夺回，成功契约的价值通常高于击杀追击。",
                "昂萨资源不可夺回，摸点是一次性归属判定；契约动作会被攻击打断，所以站位和掩护比残局追人更关键。",
                new[] { "昂萨哈凯尔", "摸点", "契约", "不可夺回" }),
            new FrontlineDecisionHintSnapshot(
                "hint.onsal.deny_enemy_contract",
                "敌方正在无垢的大地附近读契约",
                "用范围攻击或远程攻击持续覆盖契约点，优先打断 6 秒契约动作；不一定要击杀，打断本身就能改变归属。",
                "签订契约过程中受到攻击会中断，范围攻击能有效阻止敌方完成摸点。",
                new[] { "昂萨哈凯尔", "打断", "范围攻击", "防守" }),
            new FrontlineDecisionHintSnapshot(
                "hint.onsal.center_priority",
                "下一轮目标可能出现在中心区",
                "提前观察并压住 01/11/12/13，尤其是 01；中心点更容易出高等级，01 最少 A 级。",
                "中心点位高等级概率更高，且多个时间段的 A/S 刷新被限制在中心区。",
                new[] { "昂萨哈凯尔", "中心区", "高等级", "转点" }),
            new FrontlineDecisionHintSnapshot(
                "hint.onsal.phase_shift",
                "比赛进入 15:00、10:00 或 5:00 阶段切换",
                "随着启动上限减少，主团要从多点铺开转向高价值集中；最后 5 分钟只有 S 点，错误分兵会非常伤。",
                "启动上限从 6 降到 4、3、2，同时高等级概率上升，最后阶段只有 S 级。",
                new[] { "昂萨哈凯尔", "阶段", "分兵", "主团" }),
            new FrontlineDecisionHintSnapshot(
                "hint.onsal.kill_vs_ovoos",
                "追击敌方残血会错过契约点",
                "击倒收益是 +8/-8，但一个 B 点都有 50 分、A 点 100 分、S 点 200 分；没有稳定击杀时应优先摸点或打断敌方摸点。",
                "昂萨的大分差来自资源归属，击倒更适合作为保护摸点、阻止摸点或清场的手段。",
                new[] { "昂萨哈凯尔", "击倒", "机会成本", "目标优先级" }),
        },
        SummaryText = "昂萨哈凯尔（竞争战）：1400 分胜利，无垢的大地预告 30 秒、契约约 6 秒可被打断，契约后不可夺回并跳分 30 秒；S/A/B 总分 200/100/50。"
    };

    private static readonly FrontlineMapKnowledgeSnapshot FieldsOfHonorShatterKnowledge = new()
    {
        MapType = FrontlineMapType.FieldsOfHonor,
        TerritoryTypeIds = new[] { 554u },
        Name = "荣誉野（碎冰战）",
        RuleSetName = "碎冰战",
        VictoryScore = 1600,
        PrimaryObjective = "破坏随机启动的冰封石文并按仇恨量分配情报值；同时利用击倒收益扩大分差。",
        RankingRule = "最快达到 1600 情报值的阵营获胜；未到 1600 时按情报值高低排名。",
        Rules = new[]
        {
            new FrontlineKnowledgeRuleSnapshot(
                "map.shatter.victory_score",
                "胜利目标 1600 情报值",
                "荣誉野（碎冰战）中，最快累积到 1600 情报值的团队获胜；比分高低决定当前排名。",
                new[] { "荣誉野", "胜利条件", "比分" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.shatter.ice.core",
                "冰封石文是主线目标",
                "地图上设置多个拥有情报值的冰封石文，比赛开始时为停止状态，一定时间后随机启动；攻击并破坏后可获得情报值。",
                new[] { "荣誉野", "冰封石文", "主目标" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.shatter.score_by_enmity",
                "冰分按仇恨量比例分配",
                "冰封石文破坏时按仇恨量比例而不是单纯伤害比例分配情报值；所有对冰有仇恨的玩家都脱战一段时间后，冰会重置仇恨。",
                new[] { "荣誉野", "冰封石文", "仇恨", "分配" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.shatter.small_ice_efficiency",
                "小冰同伤害得分效率更高",
                "相同伤害量打小冰的得分效率约为打大冰的 2.5 倍；小冰群出现时要重新评估主团是否继续硬打大冰。",
                new[] { "荣誉野", "小冰", "效率", "转火" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.shatter.height_and_bridge",
                "桥与大冰坑高度差可被远程利用",
                "改版后荣誉野整体高度差降低，从桥跳到大冰坑中的跌落伤害约 30000；桥和大冰坑高度差小于 25 米，远程职业站桥上可以攻击下方敌人。",
                new[] { "荣誉野", "地形", "桥", "大冰坑" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.shatter.side_ice",
                "自家左侧为优势冰，右侧为劣势冰",
                "自家左侧的大冰称为优势冰，右侧的大冰称为劣势冰；右侧大冰只能通过跳崖进入，返回自家出生点附近只能从场中绕行。",
                new[] { "荣誉野", "大冰", "优势冰", "劣势冰" }),
        },
        ScoreSources = new[]
        {
            new FrontlineScoreSourceSnapshot(
                "破坏冰封石文",
                null,
                null,
                "破坏启动状态的冰封石文后按仇恨量比例分配情报值；大冰 200，小冰 50。",
                new[] { "冰封石文", "仇恨分配", "持续目标" }),
            new FrontlineScoreSourceSnapshot(
                "击倒敌方玩家",
                8,
                -8,
                "打倒其他阵营玩家后己方 +8 情报值，对方 -8 情报值。",
                new[] { "击倒", "玩家", "分差" }),
        },
        TimedSpawns = new[]
        {
            new FrontlineTimedSpawnRuleSnapshot(
                "map.shatter.spawn.big_initial",
                "开场大冰预告",
                5,
                30,
                0,
                "开场 5 秒出现 2 块大冰的启动预告，预告时间 30 秒；场上最多同时存在 2 个启动中的大冰。",
                new[] { "大冰", "开场", "预告", "2个" }),
            new FrontlineTimedSpawnRuleSnapshot(
                "map.shatter.spawn.small_initial",
                "小冰全图预告",
                420,
                30,
                0,
                "开场 420 秒，也就是剩余 13:00 时，小冰出现启动预告；剩余 12:30 时全部 15 块小冰启动。",
                new[] { "小冰", "13分钟", "12分30秒", "15个" }),
            new FrontlineTimedSpawnRuleSnapshot(
                "map.shatter.spawn.big_next",
                "后续大冰",
                null,
                30,
                0,
                "上一块大冰被破坏 5 秒后出现下一块大冰启动预告；大冰被破坏 1 分钟后复原。",
                new[] { "大冰", "循环", "5秒预告", "60秒复原" }),
            new FrontlineTimedSpawnRuleSnapshot(
                "map.shatter.spawn.small_next",
                "后续小冰",
                null,
                30,
                0,
                "小冰被破坏 6 分钟后复原，复原后 1 分钟出现启动预告。",
                new[] { "小冰", "循环", "6分钟复原", "1分钟预告" }),
        },
        ObjectiveRules = new[]
        {
            new FrontlineMapObjectiveRuleSnapshot(
                "map.shatter.objective.big_ice",
                "冰封的石文：大",
                "可破坏目标",
                200,
                null,
                null,
                "生命值 300 万，情报值 200，共 4 个；场上最多同时启动 2 个。",
                new[] { "大冰", "冰封石文", "可破坏目标" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.shatter.objective.small_ice",
                "冰封的石文：小",
                "可破坏目标",
                50,
                null,
                null,
                "生命值 30 万，情报值 50，共 15 个；剩余 12:30 时会全部启动。",
                new[] { "小冰", "冰封石文", "可破坏目标" }),
        },
        DestructibleObjectiveRules = new[]
        {
            new FrontlineDestructibleObjectiveRuleSnapshot(
                "map.shatter.destructible.big_ice",
                "冰封的石文：大",
                "大",
                4,
                2,
                3000000,
                200,
                30,
                5,
                35,
                60,
                5,
                "按仇恨量比例分配情报值",
                "开场 5 秒预告 2 块，预告 30 秒后启动；被破坏 1 分钟后复原，上一块被破坏 5 秒后预告下一块。",
                new[] { "大冰", "生命值300万", "200分", "最多2个" }),
            new FrontlineDestructibleObjectiveRuleSnapshot(
                "map.shatter.destructible.small_ice",
                "冰封的石文：小",
                "小",
                15,
                15,
                300000,
                50,
                30,
                420,
                450,
                360,
                60,
                "按仇恨量比例分配情报值",
                "开场 420 秒预告，剩余 12:30 时全部 15 块小冰启动；被破坏 6 分钟后复原，复原后 1 分钟预告。",
                new[] { "小冰", "生命值30万", "50分", "15个", "效率2.5倍" }),
        },
        LocationRules = new[]
        {
            new FrontlineMapLocationRuleSnapshot(
                "优势冰",
                new[] { "自家左侧大冰" },
                null,
                null,
                string.Empty,
                "自家左侧的大冰称为优势冰，通常路线和回撤条件更好。",
                new[] { "大冰", "优势冰", "路线" }),
            new FrontlineMapLocationRuleSnapshot(
                "劣势冰",
                new[] { "自家右侧大冰" },
                null,
                null,
                string.Empty,
                "自家右侧的大冰称为劣势冰，只能通过跳崖进入，返回自家出生点附近只能从场中绕行。",
                new[] { "大冰", "劣势冰", "路线风险" }),
            new FrontlineMapLocationRuleSnapshot(
                "桥上远程火力位",
                new[] { "桥", "大冰坑" },
                null,
                null,
                string.Empty,
                "桥和大冰坑高度差小于 25 米，远程职业站桥上可以攻击下方敌人；跳下大冰坑跌落伤害约 30000。",
                new[] { "地形", "桥", "远程", "跌落" }),
        },
        DecisionHints = new[]
        {
            new FrontlineDecisionHintSnapshot(
                "hint.shatter.small_ice_efficiency",
                "小冰启动且我方仍在打大冰",
                "立即评估是否分队或转火小冰；相同伤害量打小冰的得分效率约为大冰 2.5 倍，放任敌方吃小冰会快速拉开分差。",
                "小冰 30 万血 50 分，大冰 300 万血 200 分，单位伤害换分小冰显著更高。",
                new[] { "荣誉野", "小冰", "效率", "转火" }),
            new FrontlineDecisionHintSnapshot(
                "hint.shatter.big_ice_next",
                "上一块大冰即将被破坏或刚被破坏",
                "提前看下一块大冰路线，5 秒后就会出现下一块大冰预告；不要等结算后全团原地发呆。",
                "上一块大冰被破坏 5 秒后出现下一块大冰启动预告，大冰节奏转换很快。",
                new[] { "荣誉野", "大冰", "转点", "节奏" }),
            new FrontlineDecisionHintSnapshot(
                "hint.shatter_enmity_score",
                "我方多人正在摸冰但仇恨可能断开",
                "保持对冰的持续仇恨，避免所有人脱战导致冰重置仇恨；需要把冰分理解为仇恨贡献而不是纯伤害快照。",
                "冰分按仇恨量比例分配，所有对冰有仇恨的玩家均脱战一段时间后会重置仇恨。",
                new[] { "荣誉野", "仇恨", "冰分", "重置" }),
            new FrontlineDecisionHintSnapshot(
                "hint.shatter_disadvantage_ice",
                "准备争夺自家右侧劣势冰",
                "进入前先判断撤退路线和第三方夹击风险；右侧劣势冰只能跳崖进入，回自家出生点附近要从场中绕行。",
                "劣势冰路线天然更差，打赢之前容易，撤出来才是风险点。",
                new[] { "荣誉野", "劣势冰", "路线", "撤退" }),
            new FrontlineDecisionHintSnapshot(
                "hint.shatter_kill_vs_ice",
                "追击残血敌人会丢冰仇恨或小冰窗口",
                "击倒收益是 +8/-8，但冰块是主线收益；除非追击能阻止敌方拿冰或形成清场，否则优先维持冰仇恨和转火高效率目标。",
                "碎冰战的主要分差来自冰块分配，击倒更适合作为保护打冰和阻止敌方输出冰的工具。",
                new[] { "荣誉野", "击倒", "冰分", "机会成本" }),
        },
        SummaryText = "荣誉野（碎冰战）：1600 分胜利，大冰 300万HP/200分/共4个，小冰 30万HP/50分/共15个；冰分按仇恨量比例分配，小冰单位伤害换分约为大冰 2.5 倍。"
    };

    private static readonly FrontlineMapKnowledgeSnapshot VochesterTrainingKnowledge = new()
    {
        MapType = FrontlineMapType.Vochester,
        TerritoryTypeIds = new[] { 1313u },
        Name = "沃刻其特（演习战）",
        RuleSetName = "演习战",
        VictoryScore = 1400,
        PrimaryObjective = "控制随机激活的战略目标点并利用天气变化创造窗口；目标点一旦控制不可被敌方夺回。",
        RankingRule = "率先获得 1400 战略值的连队获胜；限定时间内未决胜则按战略值高低排名。",
        Rules = new[]
        {
            new FrontlineKnowledgeRuleSnapshot(
                "map.vochester.victory_score",
                "胜利目标 1400 战略值",
                "沃刻其特（演习战）通过控制战略目标点或击倒敌方玩家获得战略值，率先获得 1400 战略值的连队获胜。",
                new[] { "沃刻其特", "胜利条件", "比分" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.vochester.objective.count",
                "12 个战略目标点",
                "沃刻其特共有 12 个战略目标点，编号 01-12；版本 7.41 后，地图中央附近的位置更容易出现可控制的战略目标点。",
                new[] { "沃刻其特", "战略目标点", "12点" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.vochester.objective.states",
                "战略目标点固定四阶段",
                "战略目标点必定按失效、过渡、可控制、控制状态顺序变化；过渡状态持续 30 秒并显示可控制倒计时。",
                new[] { "沃刻其特", "状态", "过渡", "可控制" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.vochester.control.cannot_recapture",
                "控制后不可夺回",
                "战略目标点进入控制状态后无法被敌方夺回；经过 30 秒跳分后自动解除控制并回到失效状态，随后别处失效目标点会即刻进入过渡状态。",
                new[] { "沃刻其特", "控制", "不可夺回", "刷新链" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.vochester.capture_area",
                "占领区域固定 8 秒",
                "可控制状态的目标点周围会出现多个占领区域；单一连队成员停留时出现抢占进度槽，8 秒后完成占领，速度与人数无关。",
                new[] { "沃刻其特", "占领区域", "8秒", "人数无关" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.vochester.contested_capture",
                "敌方进入会暂停抢占",
                "抢占方离开会清空进度；敌方进入区域会让抢占暂停并进入抢夺中，敌方在己方离开后需等原进度清零再从零计数。",
                new[] { "沃刻其特", "抢占", "抢夺中", "暂停" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.vochester.weather.core",
                "天气是地图核心变量",
                "战场天气会随时间变化，小雪和极光分别影响地形/伤害事件、防护罩、高等级目标概率和极限槽增长。",
                new[] { "沃刻其特", "天气", "小雪", "极光" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.vochester.landing_point.invasion",
                "入侵敌方登陆点会持续掉血",
                "进入敌方登陆点会获得入侵敌营状态并受到每次 30000 的持续伤害；若因此死亡，只有己方失去战略值，敌方不会获得战略值。",
                new[] { "沃刻其特", "登陆点", "入侵敌营", "30000伤害" }),
            new FrontlineKnowledgeRuleSnapshot(
                "map.vochester.battle_notice",
                "战场通告是重要信息源",
                "各连队控制战略目标点、天气发生变化等情报会显示在战场通告窗口中，后续采集可优先从这里补事件时间线。",
                new[] { "沃刻其特", "战场通告", "事件采集" }),
        },
        ObjectiveRankScores = new[]
        {
            new FrontlineObjectiveRankScoreSnapshot("S", 200, 20, 3, 10, 30),
            new FrontlineObjectiveRankScoreSnapshot("A", 100, 10, 3, 10, 30),
            new FrontlineObjectiveRankScoreSnapshot("B", 50, 5, 3, 10, 30),
        },
        ScoreSources = new[]
        {
            new FrontlineScoreSourceSnapshot(
                "控制战略目标点",
                null,
                null,
                "控制可控制状态的战略目标点后持续 30 秒获得战略值；S/A/B 总分分别为 200/100/50。",
                new[] { "战略目标点", "持续收益", "控制" }),
            new FrontlineScoreSourceSnapshot(
                "击倒敌方玩家",
                8,
                -8,
                "击倒任意敌方玩家后己方 +8 战略值，被击倒玩家所属连队 -8 战略值。",
                new[] { "击倒", "玩家", "分差" }),
            new FrontlineScoreSourceSnapshot(
                "天气或环境死亡",
                0,
                -8,
                "入侵敌营 DOT 或小雪雪人伤害导致死亡时，只有死亡者所属连队失去 8 战略值，敌方不会获得战略值。",
                new[] { "天气", "环境伤害", "扣分" }),
        },
        TimedSpawns = new[]
        {
            new FrontlineTimedSpawnRuleSnapshot(
                "map.vochester.objective.transition",
                "战略目标点过渡",
                null,
                30,
                30,
                "目标点进入过渡状态后持续 30 秒，倒计时结束进入可控制状态；控制状态持续 30 秒跳分。",
                new[] { "战略目标点", "过渡", "30秒" }),
            new FrontlineTimedSpawnRuleSnapshot(
                "map.vochester.weather.notice",
                "天气变化通告",
                null,
                15,
                0,
                "小雪和极光开始前 15 秒会出现倒计时通告，结束时也会出现天气结束通告。",
                new[] { "天气", "通告", "15秒" }),
        },
        ObjectiveRules = new[]
        {
            new FrontlineMapObjectiveRuleSnapshot(
                "map.vochester.objective.point",
                "战略目标点",
                "控制点",
                null,
                null,
                30,
                "失效、过渡、可控制、控制四阶段循环；控制后不可被夺回，30 秒后自动失效并触发别处过渡。",
                new[] { "战略目标点", "控制点", "不可夺回" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.vochester.objective.rank_s",
                "S 级战略目标点",
                "高价值控制点",
                200,
                null,
                30,
                "周围出现 5 个占领区域，总计 200 战略值，每 3 秒 20 分，共 10 跳。",
                new[] { "战略目标点", "S级", "5区域", "高价值" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.vochester.objective.rank_a",
                "A 级战略目标点",
                "中高价值控制点",
                100,
                null,
                30,
                "周围出现 4 个占领区域，总计 100 战略值，每 3 秒 10 分，共 10 跳。",
                new[] { "战略目标点", "A级", "4区域" }),
            new FrontlineMapObjectiveRuleSnapshot(
                "map.vochester.objective.rank_b",
                "B 级战略目标点",
                "基础控制点",
                50,
                null,
                30,
                "周围出现 3 个占领区域，总计 50 战略值，每 3 秒 5 分，共 10 跳。",
                new[] { "战略目标点", "B级", "3区域" }),
        },
        PhaseRules = new[]
        {
            new FrontlineMapPhaseRuleSnapshot(
                "19:55-15:00",
                5,
                300,
                6,
                "B",
                "激活上限 6 个，A/B 级均有；只有 01/02/03 和 10/11/12 会出现 A 级。",
                new[] { "阶段", "前期", "6个", "AB" }),
            new FrontlineMapPhaseRuleSnapshot(
                "15:00-10:00",
                300,
                600,
                5,
                "B",
                "激活上限 5 个，A/B 级均有；天气和中心区域会影响高等级目标概率。",
                new[] { "阶段", "中期", "5个", "AB" }),
            new FrontlineMapPhaseRuleSnapshot(
                "10:00-5:00",
                600,
                900,
                3,
                "B",
                "激活上限 3 个，大部分为 A 级，小部分为 S 级，B 级罕见；只有 01/02/03 和 10/11/12 会出现 S 级。",
                new[] { "阶段", "后期", "3个", "A多S少" }),
            new FrontlineMapPhaseRuleSnapshot(
                "5:00-0:00",
                900,
                1200,
                2,
                "A",
                "激活上限 2 个，S/A 级均有；最后阶段目标数量少，错误分兵代价很高。",
                new[] { "阶段", "决胜", "2个", "SA" }),
        },
        LocationRules = new[]
        {
            new FrontlineMapLocationRuleSnapshot(
                "中央目标点",
                new[] { "01", "02", "03" },
                300,
                null,
                "A",
                "战斗开始 5 分钟后，地图中部 01/02/03 三个战略目标点最低为 A 级；版本 7.41 后中央附近更容易出现可控制目标点。",
                new[] { "中央", "01", "02", "03", "至少A级" }),
            new FrontlineMapLocationRuleSnapshot(
                "前期 A 级限定区",
                new[] { "01", "02", "03", "10", "11", "12" },
                5,
                300,
                "A",
                "19:55-15:00 期间，只有 01/02/03 和 10/11/12 会出现 A 级战略目标点。",
                new[] { "A级", "前期", "限定点位" }),
            new FrontlineMapLocationRuleSnapshot(
                "后期 S 级限定区",
                new[] { "01", "02", "03", "10", "11", "12" },
                600,
                900,
                "S",
                "10:00-5:00 期间，只有 01/02/03 和 10/11/12 会出现 S 级战略目标点。",
                new[] { "S级", "后期", "限定点位" }),
        },
        WeatherRules = new[]
        {
            new FrontlineWeatherRuleSnapshot(
                "小雪",
                0,
                300,
                "小雪持续 5 分钟，每场战斗仅出现一次且在前半场；开始前 15 秒通告。雪精会生成雪人，雪人伤害无视防护罩/减伤/保护/神圣领域；小/中/大雪人伤害约 30000/50000/400000，命中会赋予短暂不可骑乘。雪精的祝福提供持续 30 秒、相当于最大体力 50% 的护盾。",
                new[] { "天气", "小雪", "雪人", "护盾", "前半场" }),
            new FrontlineWeatherRuleSnapshot(
                "极光",
                0,
                150,
                "极光持续 2.5 分钟，每场战斗仅出现一次且在后半场；开始前 15 秒通告。极光期间更容易出现高等级战略目标点，全体玩家获得极限槽增长，极限槽增长速度为非极光天气的 2 倍。",
                new[] { "天气", "极光", "高等级", "极限槽", "后半场" }),
        },
        DecisionHints = new[]
        {
            new FrontlineDecisionHintSnapshot(
                "hint.vochester.capture_area_cover",
                "目标点进入可控制状态且我方准备抢区域",
                "优先安排队友覆盖敌方进入路线，保护抢占者完成固定 8 秒进度；人数不会加速，占领区域内稳定不被干扰更重要。",
                "占领速度与区域内玩家人数无关，敌方进入会暂停抢占，己方离开会清空进度。",
                new[] { "沃刻其特", "占领区域", "8秒", "掩护" }),
            new FrontlineDecisionHintSnapshot(
                "hint.vochester.objective_locked",
                "目标点已经被任一阵营控制",
                "不要继续投入夺回该目标点，除非目的是击杀或切断敌方撤退；控制状态不可被夺回，应准备下一轮过渡目标。",
                "目标点控制后无法被敌方夺回，30 秒后失效并触发别处目标点过渡。",
                new[] { "沃刻其特", "不可夺回", "转点" }),
            new FrontlineDecisionHintSnapshot(
                "hint.vochester_snow",
                "小雪天气即将开始或正在持续",
                "利用雪人作为障碍物，同时避开雪人落点；雪人击杀只会让死亡者所属连队扣分，不会给敌方加分。",
                "雪人伤害无视常规减伤/护盾/保护类效果，且小雪期间会出现雪精的祝福护盾。",
                new[] { "沃刻其特", "小雪", "雪人", "地形" }),
            new FrontlineDecisionHintSnapshot(
                "hint.vochester_aurora",
                "极光天气即将开始或正在持续",
                "提前向高价值目标点靠拢，并把敌我极限技压力按 2 倍增长速度重新估算；极光期间高等级目标概率也会上升。",
                "极光会让全体玩家极限槽增长速度变为非极光天气的 2 倍，并提高高等级战略目标点出现概率。",
                new[] { "沃刻其特", "极光", "极限槽", "高等级" }),
            new FrontlineDecisionHintSnapshot(
                "hint.vochester_landing_point",
                "队伍追击进入敌方登陆点附近",
                "避免深追进敌方登陆点；入侵敌营 DOT 每次约 30000，因该 DOT 死亡只扣己方分，不给敌方加分但仍是净亏。",
                "敌方登陆点入侵状态会持续掉血，死亡会让己方失去 8 战略值。",
                new[] { "沃刻其特", "登陆点", "追击", "扣分" }),
        },
        SummaryText = "沃刻其特（演习战）：1400 分胜利，12 个战略目标点，过渡 30 秒、占领区域固定 8 秒、控制后不可夺回并跳分 30 秒；小雪偏地形/环境伤害，极光提高高等级目标和极限槽速度。"
    };

    private static readonly FrontlineMapKnowledgeSnapshot[] MapKnowledge =
    {
        BorderlandRuinsSecureKnowledge,
        SealRockSeizeKnowledge,
        OnsalHakairDanshigNaadamKnowledge,
        FieldsOfHonorShatterKnowledge,
        VochesterTrainingKnowledge,
    };

    private static readonly FrontlineDecisionHintSnapshot[] DecisionHints =
    {
        new(
            "hint.respawn.tempo_not_permanent",
            "统计到我方或敌方多人死亡",
            "把死亡人数理解为短时间战斗力差，而不是永久人数差；结合最近死亡/复活节奏判断推进窗口。",
            "前线死亡后会从出生点无限复活，真正关键的是回场时间差和大团是否脱节。",
            new[] { "复活", "节奏", "推进窗口" }),
        new(
            "hint.spawn.no_overchase",
            "敌方大团退回出生点附近或敌人刚离开出生点",
            "避免继续深追出生点目标，优先转向可得分目标、夹击路线或安全撤退。",
            "出生点内是无限无敌，离开出生点后仍有 10 秒无敌，追击收益很低且容易被反包。",
            new[] { "出生点", "追击", "无敌", "转点" }),
        new(
            "hint.friendly.regroup_at_spawn",
            "我方大量阵亡且复活节奏集中",
            "可以把出生点视为安全重整点，等待关键人数复活后再组织下一波出门。",
            "出生点内无敌且免疫击退/牵引/摔落伤害，适合重整，但离开后保护只有 10 秒。",
            new[] { "出生点", "重整", "复活节奏" }),
        new(
            "hint.return.escape_or_reset",
            "我方被追击且附近没有敌方直接攻击压力",
            "可以建议使用返回回出生点重整；如果敌人贴脸或有远程普攻/技能压力，则不要把返回视为稳定逃生。",
            "前线返回无复唱且战斗中可用，但读条被攻击会中断。",
            new[] { "返回", "撤退", "重整" }),
        new(
            "hint.return.interrupt_enemy",
            "敌方正在咏唱返回",
            "若需要留下目标，应使用能造成直接攻击判定的技能打断；不要依赖蛊毒法、野火这类不会打断返回的效果。",
            "返回读条受到攻击会被打断，但部分附加效果不会触发打断。",
            new[] { "返回", "打断", "集火" }),
        new(
            "hint.mount.rotation_speed",
            "判断敌我大团转点速度",
            "默认按地面坐骑速度估算转点，不区分坐骑种类；战斗状态不阻止重新上马，只有被攻击后的 5 秒锁骑乘会拖慢节奏。",
            "前线所有坐骑速度相同，且战斗中也可以骑乘。",
            new[] { "坐骑", "转点", "节奏" }),
        new(
            "hint.mount.catch_window",
            "敌方骑乘目标进入我方攻击范围",
            "可以用任意有效攻击把目标打下马，制造 5 秒不可骑乘窗口，方便拖住落单目标或减慢敌方转点。",
            "骑乘中受到攻击会强制下马，并 5 秒无法再次骑乘。",
            new[] { "坐骑", "拦截", "追击" }),
        new(
            "hint.battle_high.target_value",
            "发现敌方高斗志昂扬目标",
            "高战意目标击倒收益更高，但其伤害和治疗也更强；只有在我方人数、控制和爆发足够时才应主动集火。",
            "击倒斗志昂扬 V 目标可获得 16 战意，助攻 8；但 V 级自身伤害和治疗提高 50%。",
            new[] { "战意", "斗志昂扬", "集火", "风险" }),
        new(
            "hint.battle_high.protect_friendly",
            "我方出现斗志昂扬 IV/V 玩家",
            "优先保护高战意队友，避免其因切职或掉线重连清空战意；围绕其爆发窗口推进更划算。",
            "7.5 后死亡不掉战意，高战意本身是可持续优势，但切职和掉线重连会清空。",
            new[] { "战意", "保护", "推进" }),
        new(
            "hint.limit_break.score_rank",
            "判断三方极限技威胁",
            "比分第三名的极限技压力要按更快积蓄估算，第一名则更慢；同分时按排名靠前的较慢规则处理。",
            "第一名极限技积蓄速度 -25%，第二名正常，第三名 +25%。",
            new[] { "极限技", "比分", "威胁评估" }),
        new(
            "hint.job_adjustment.target_selection",
            "选择集火目标",
            "结合职业生命值和前线受伤补正评估击杀难度；坦克和蝰蛇等受伤补正很高，低血量远敏/法系/治疗通常更适合作为突破口。",
            "前线有独立职业补正，比例伤害和即死类以外的伤害都会受此系数影响。",
            new[] { "职业补正", "集火", "目标选择" }),
        new(
            "hint.crowd_control.shorter_window",
            "依赖控制链开团或留人",
            "控制窗口要按减少 25% 估算，不要把水晶冲突或技能面板上的完整持续时间直接当作前线持续时间。",
            "前线眩晕、加重、止步、沉默、睡眠、冻结持续时间缩短 25%。",
            new[] { "控制", "开团", "留人" }),
        new(
            "hint.defense.break_group",
            "敌方多人开防御或抱团硬吃我方爆发",
            "如果我方有战士、舞者、钐镰客，可以把群体解除防御技能视为开团或反打窗口；没有破防工具时应避免把主要爆发浪费在防御目标上。",
            "原初的怒号、行列舞、暗夜游魂可以群体解除他人防御。",
            new[] { "防御", "破防", "开团", "反打" }),
        new(
            "hint.defense.break_single",
            "单个关键目标开防御保命",
            "可以优先调度武僧陨石冲击或防护职业全力挥打处理单体防御目标，再衔接集火。",
            "陨石冲击、全力挥打可以单体解除他人防御。",
            new[] { "防御", "破防", "集火" }),
        new(
            "hint.defense.ignore_machinist",
            "敌方关键目标依赖防御减伤拖时间",
            "如果我方机工士位置合适，钻头可以作为无视防御减伤的补刀或压血工具。",
            "机工士钻头可以无视防御减伤造成伤害。",
            new[] { "防御", "机工士", "钻头", "补刀" }),
    };

    public static FrontlineKnowledgeSnapshot GetSnapshot(uint territoryType, uint mapId)
    {
        var currentMap = ResolveCurrentMapKnowledge(territoryType, mapId);
        var currentMapText = currentMap == null ? string.Empty : $"，当前地图：{currentMap.Name}";

        return new FrontlineKnowledgeSnapshot
        {
            GlobalRules = GlobalRules,
            DecisionHints = DecisionHints,
            CommanderMacroIntents = CommanderMacroIntents,
            BattlefieldProfile = BattlefieldProfile,
            BattleHighRewards = BattleHighRewards,
            BattleHighTiers = BattleHighTiers,
            LimitBreakRankRules = LimitBreakRankRules,
            JobAdjustments = JobAdjustments,
            CrowdControlAdjustments = CrowdControlAdjustments,
            DefenseInteractionSkills = DefenseInteractionSkills,
            KeySkillRules = KeySkillRules,
            CurrentMap = currentMap,
            KnownMaps = MapKnowledge,
            SummaryText = $"已加载通用前线知识 {GlobalRules.Length} 条，决策提示 {DecisionHints.Length} 条，指挥宏意图 {CommanderMacroIntents.Length} 类，职业补正 {JobAdjustments.Length} 项，防御交互 {DefenseInteractionSkills.Length} 项，关键技能 {KeySkillRules.Length} 项，地图知识 {MapKnowledge.Length} 张{currentMapText}"
        };
    }

    private static FrontlineMapKnowledgeSnapshot? ResolveCurrentMapKnowledge(uint territoryType, uint mapId)
    {
        _ = mapId;
        foreach (var map in MapKnowledge)
        {
            foreach (var id in map.TerritoryTypeIds)
            {
                if (id == territoryType)
                    return map;
            }
        }

        return null;
    }

    private static FrontlineJobAdjustmentSnapshot CreateJobAdjustment(
        uint classJobId,
        string jobName,
        int maxHp,
        int outgoingDamageModifierPercent,
        int incomingDamageModifierPercent,
        int baseLimitBreakChargeSeconds,
        int frontlineLimitBreakChargeModifierSeconds)
        => new(
            classJobId,
            jobName,
            maxHp,
            outgoingDamageModifierPercent,
            incomingDamageModifierPercent,
            baseLimitBreakChargeSeconds,
            frontlineLimitBreakChargeModifierSeconds,
            baseLimitBreakChargeSeconds + frontlineLimitBreakChargeModifierSeconds);

    private static FrontlineKeySkillRuleSnapshot CreateKeySkillRule(
        string id,
        uint? classJobId,
        string jobName,
        string skillName,
        BattlefieldKeySkillKind kind,
        int cooldownSeconds,
        float baseThreat,
        bool controlChain,
        bool defenseBreak,
        bool executeWindow,
        bool areaPressure,
        string tacticalNote,
        params string[] tags)
        => new(
            id,
            classJobId,
            jobName,
            skillName,
            kind,
            Math.Max(1, cooldownSeconds),
            Math.Clamp(baseThreat, 0f, 100f),
            controlChain,
            defenseBreak,
            executeWindow,
            areaPressure,
            tacticalNote,
            "灰机Wiki 对战技能",
            PvpActionsWikiSource,
            tags);
}
