#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEditor.Animations;

#if YOZOLAB_VRCSDK_BASE && YOZOLAB_VRCSDK_AVATARS && YOZOLAB_AACV1
using AnimatorAsCode.V1;

namespace YozoLab.AAC.Template
{
    public static class ylaac
    {
        /// <summary>
        /// https://discussions.unity.com/t/enabling-normalized-blend-values-via-script/813452/5
        /// </summary>
        public static AacFlBlendTreeDirect WithNormalizeBlendValues(this AacFlBlendTreeDirect direct)
        {
            if (direct?.BlendTree != null)
            {
                using (var so = new SerializedObject(direct.BlendTree))
                {
                    var prop = so.FindProperty("m_NormalizedBlendValues");
                    if (prop != null)
                    {
                        prop.boolValue = true;
                        so.ApplyModifiedProperties();
                    }
                }
            }
            return direct;
        }

        public static AacFlBlendTree1D And(
            AacFlBase Base,
            AacFlFloatParameter inputA,
            AacFlFloatParameter inputB,
            AacFlFloatParameter output)
        {
            AacFlClip output0 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(0.0f)).NonLooping();
            AacFlClip output1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();

            AacFlBlendTree1D bt = Base.NewBlendTree().Simple1D(inputA)
                .WithAnimation(output0, 0)
                .WithAnimation(Base.NewBlendTree().Simple1D(inputB)
                    .WithAnimation(output0, 0)
                    .WithAnimation(output1, 1), 1);
            return bt;
        }

        public static AacFlBlendTree1D Or(
            AacFlBase Base,
            AacFlFloatParameter inputA,
            AacFlFloatParameter inputB,
            AacFlFloatParameter output)
        {
            AacFlClip output0 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(0.0f)).NonLooping();
            AacFlClip output1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();

            AacFlBlendTree1D bt = Base.NewBlendTree().Simple1D(inputA)
                .WithAnimation(Base.NewBlendTree().Simple1D(inputB)
                    .WithAnimation(output0, 0)
                    .WithAnimation(output1, 1), 0)
                .WithAnimation(output1, 1);
            return bt;
        }

        public static AacFlBlendTree1D Not(
            AacFlBase Base,
            AacFlFloatParameter input,
            AacFlFloatParameter output)
        {
            AacFlClip output0 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(0.0f)).NonLooping();
            AacFlClip output1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();

            AacFlBlendTree1D bt = Base.NewBlendTree().Simple1D(input)
                .WithAnimation(output1, 0)
                .WithAnimation(output0, 1);
            return bt;
        }

        public static AacFlBlendTree1D Remap(
            AacFlBase Base,
            AacFlFloatParameter input,
            AacFlFloatParameter output,
            float min,
            float max,
            float min_threshold = 0.0f,
            float max_threshold = 1.0f)
        {
            AacFlClip output_min = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(min)).NonLooping();
            AacFlClip output_max = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(max)).NonLooping();

            AacFlBlendTree1D bt = Base.NewBlendTree().Simple1D(input)
                .WithAnimation(output_min, min_threshold)
                .WithAnimation(output_max, max_threshold);
            return bt;
        }

        /// <summary>
        /// 2つの入力を加算するDirectBlendtreeを作成します。
        /// </summary>
        /// <remarks>
        /// <b>注意:</b> 加算にNegative値を使用する場合、Base,inputA,inputB,output,layer,min,maxという引数を指定しなければなりません。<para/>
        /// 詳しくは、ドキュメントを参照してください。https://vrc.school/docs/Other/Advanced-BlendTrees#1b6b02e8d0d8431eb1bd26c5a95831da
        /// </remarks>
        public static AacFlBlendTreeDirect Add(
            AacFlBase Base,
            AacFlFloatParameter inputA,
            AacFlFloatParameter inputB,
            AacFlFloatParameter output)
        {
            AacFlClip output1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();

            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct()
                .WithAnimation(output1, inputA)
                .WithAnimation(output1, inputB);
            return bt;
        }

        /// <summary>
        /// 2つの入力を加算するDirectBlendtreeを作成します。
        /// </summary>
        /// <remarks>
        /// <b>注意:</b> 加算にNegative値を使用する場合、Base,inputA,inputB,output,layer,min,maxという引数を指定しなければなりません。<para/>
        /// 詳しくは、ドキュメントを参照してください。https://vrc.school/docs/Other/Advanced-BlendTrees#1b6b02e8d0d8431eb1bd26c5a95831da
        /// </remarks>
        public static AacFlBlendTreeDirect Add(
            AacFlBase Base,
            AacFlFloatParameter inputA,
            AacFlFloatParameter inputB,
            AacFlFloatParameter output,
            AacFlLayer layer,
            float min,
            float max)
        {
            AacFlFloatParameter one = layer.FloatParameter("One");
            layer.OverrideValue(one, 1.0f);

            AacFlClip output_min = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(min)).NonLooping();
            AacFlClip output_max = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(max)).NonLooping();

            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct()
                .WithAnimation(Base.NewBlendTree().Simple1D(inputA)
                    .WithAnimation(output_min, min)
                    .WithAnimation(output_max, max), one)
                .WithAnimation(Base.NewBlendTree().Simple1D(inputB)
                    .WithAnimation(output_min, min)
                    .WithAnimation(output_max, max), one);
            return bt;
        }

        /// <summary>
        /// inputA - inputB = output
        /// </summary>
        public static AacFlBlendTreeDirect Sub(
            AacFlBase Base,
            AacFlFloatParameter inputA,
            AacFlFloatParameter inputB,
            AacFlFloatParameter output)
        {
            AacFlClip output1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();
            AacFlClip outputminus1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(-1.0f)).NonLooping();

            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct()
                .WithAnimation(output1, inputA)
                .WithAnimation(outputminus1, inputB);
            return bt;
        }

        public static AacFlBlendTreeDirect Sub(
            AacFlBase Base,
            AacFlFloatParameter inputA,
            AacFlFloatParameter inputB,
            AacFlFloatParameter output,
            AacFlLayer layer,
            float min,
            float max)
        {
            AacFlFloatParameter one = layer.FloatParameter("One");
            layer.OverrideValue(one, 1.0f);

            AacFlClip output_min = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(min)).NonLooping();
            AacFlClip output_max = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(max)).NonLooping();

            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct()
                .WithAnimation(Base.NewBlendTree().Simple1D(inputA)
                    .WithAnimation(output_min, min)
                    .WithAnimation(output_max, max), one)
                .WithAnimation(Base.NewBlendTree().Simple1D(inputB)
                    .WithAnimation(output_max, min)
                    .WithAnimation(output_min, max), one);
            return bt;
        }

        /// <remarks>
        /// <b>注意:</b> Negativeを含むMultiには対応していません。<para/>
        /// 詳しくは、ドキュメントを参照してください。https://vrc.school/docs/Other/Advanced-BlendTrees#47d92e363be340f799fd95b98eb9c337
        /// </remarks>
        public static AacFlBlendTreeDirect Multi(
            AacFlBase Base,
            AacFlFloatParameter inputA,
            AacFlFloatParameter inputB,
            AacFlFloatParameter output)
        {
            AacFlClip output1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();

            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct()
                .WithAnimation(Base.NewBlendTree().Direct()
                    .WithAnimation(output1, inputB), inputA);
            return bt;
        }

        /// <summary>
        /// この関数は、逆数を返すBlendtreeの原型です。範囲は (1<n<infinity) です。計算に1Fかかります。 <para/>
        /// 式は 1/(1+Input) という形になっていることに注意してください。https://vrc.school/docs/Other/Advanced-BlendTrees#7229acb83ba7465dbd50a2236b134031
        /// </summary>
        public static AacFlBlendTreeDirect Inv_GreaterThanOne_PlusOne(
            AacFlBase Base,
            AacFlFloatParameter input,
            AacFlFloatParameter output,
            AacFlLayer layer)
        {
            AacFlFloatParameter one = layer.FloatParameter("One");
            layer.OverrideValue(one, 1.0f);

            AacFlClip Dummy = Base.NewClip().NonLooping();
            AacFlClip output1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();

            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct().WithNormalizeBlendValues()
                .WithAnimation(Dummy, input)
                .WithAnimation(output1, one);
            return bt;
        }

        /// <summary>
        /// この関数は、逆数を返すBlendtreeです。範囲は (1<n<infinity) です。計算に2Fかかります。<para
        /// </summary>
        public static AacFlBlendTreeDirect Inv_GreaterThanOne(
            AacFlBase Base,
            AacFlFloatParameter input,
            AacFlFloatParameter output,
            AacFlLayer layer)
        {
            AacFlFloatParameter one = layer.FloatParameter("One");
            layer.OverrideValue(one, 1.0f);

            AacFlFloatParameter outputProxy = layer.FloatParameter($"{output.Name}_InvProxy");

            AacFlBlendTreeDirect Sub_DBT = Sub(Base, input, one, outputProxy);
            AacFlBlendTreeDirect Div_BBT = Inv_GreaterThanOne_PlusOne(Base, outputProxy, output, layer);

            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct()
                .WithAnimation(Sub_DBT, one)
                .WithAnimation(Div_BBT, one);
            return bt;
        }

        /// <summary>
        /// この関数は逆数を返すBlendtreeです。範囲は (0<n<infinity) です。
        /// 生成過程で新たに一つレイヤーを生成します。
        /// </summary>
        public static AacFlBlendTreeDirect Inv(
            AacFlBase Base,
            AacFlFloatParameter input,
            AacFlFloatParameter output,
            AacFlLayer layer,
            AnimatorController AssetContainer)
        {
            AacFlBlendTreeDirect bt = Inv_GreaterThanOne(Base, input, output, layer);

            //NewLayer
            AacFlLayer newlayer = Base.CreateSupportingArbitraryControllerLayer(AssetContainer, $"{output.Name}_ILTO");
            AacFlClip Dummy = Base.NewClip().NonLooping();
            AacFlClip ILTOClip = InverseLessThanOne_AsClip(Base, output);

            AacFlState DummyState = newlayer.NewState("Dummy_State").WithAnimation(Dummy);
            AacFlState ILTOState = newlayer.NewState("ILTO_State").WithAnimation(ILTOClip).WithMotionTime(input);

            DummyState.TransitionsTo(ILTOState)
                .When(input.IsLessThan(1.0f));
            ILTOState.TransitionsTo(DummyState)
                .When(input.IsGreaterThan(1.0f));
            return bt;
        }

        /// <summary>
        /// この関数はinputA / inputB = outputを返すBlendtreeです。
        /// </summary>
        public static AacFlBlendTreeDirect Div(
            AacFlBase Base,
            AacFlFloatParameter inputA,
            AacFlFloatParameter inputB,
            AacFlFloatParameter output,
            AacFlLayer layer,
            AnimatorController AssetContainer)
        {
            AacFlFloatParameter one = layer.FloatParameter("One");
            layer.OverrideValue(one, 1.0f);

            AacFlFloatParameter outputProxy = layer.FloatParameter($"{output.Name}_DivProxy");

            AacFlBlendTreeDirect InvInput = Inv(Base, inputB, outputProxy, layer, AssetContainer);
            AacFlBlendTreeDirect MultInput = Multi(Base, inputA, outputProxy, output);
            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct()
                .WithAnimation(InvInput, one)
                .WithAnimation(MultInput, one);
            return bt;
        }

        /// <summary>
        /// FlameTimeを提供する関数です。
        /// timeは、全体で利用する経過時間のパラメーターを指定します。このパラメータは1sごとに1増加します。
        /// outputは、フレームタイムとして扱いたいパラメータを指定します。
        /// この関数はひとつのAnimatorControllerにつき一回のみ呼ばれるべきです。
        /// https://vrc.school/docs/Other/Advanced-BlendTrees#f038ff5bbe1243d69c0bb2b1c7b7bc2c
        /// </summary>
        public static AacFlBlendTreeDirect FrameTime(
            AacFlBase Base,
            AacFlFloatParameter time, //全体で利用する経過時間のパラメーター
            AacFlFloatParameter frametime, //flametime
            AacFlLayer layer,
            AnimatorController AssetContainer //todo:AnimatorContlollerに
        )
        {
            AacFlFloatParameter lasttime = layer.FloatParameter($"{frametime.Name}_LastTime");

            //NewLayer
            AacFlLayer newlayer = Base.CreateSupportingArbitraryControllerLayer(AssetContainer, $"{time.Name}_Time");
            float frameStart = 0f;
            float frameEnd = 120000f;
            float valueStart = 0f;
            float valueEnd = 2000f;
            float frameRate = 60f; // Unity標準
            float timeStart = frameStart / frameRate; // 秒
            float timeEnd = frameEnd / frameRate;     // 秒
            AnimationCurve curve = new AnimationCurve();
            Keyframe keyStart = new Keyframe(timeStart, valueStart);
            Keyframe keyEnd = new Keyframe(timeEnd, valueEnd);
            keyStart.inTangent = keyStart.outTangent = (valueEnd - valueStart) / (timeEnd - timeStart);
            keyEnd.inTangent = keyEnd.outTangent = (valueEnd - valueStart) / (timeEnd - timeStart);
            curve.AddKey(keyStart);
            curve.AddKey(keyEnd);
            AacFlClip TimeClip = Base.NewClip()
                .Looping()
                .Animating(a =>
                    a.AnimatesAnimator(time).WithAnimationCurve(curve));
            AacFlState TimeState = newlayer.NewState("Time_State").WithAnimation(TimeClip);

            //DBT
            AacFlClip output1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(frametime).WithOneFrame(1.0f)).NonLooping();
            AacFlClip outputNegative1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(frametime).WithOneFrame(-1.0f)).NonLooping();
            AacFlClip lasttime1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(lasttime).WithOneFrame(1.0f)).NonLooping();
            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct()
                .WithAnimation(output1, time)
                .WithAnimation(outputNegative1, lasttime)
                .WithAnimation(lasttime1, time);
            return bt;
        }

        public static AacFlBlendTree Smooth(
            AacFlBase Base,
            AacFlFloatParameter smoothAmount,
            AacFlFloatParameter input,
            AacFlFloatParameter output)
        {
            AacFlClip clipSmoothOutput0 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(0.0f)).NonLooping();
            AacFlClip clipSmoothOutput1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();

            AacFlBlendTree bt = Base.NewBlendTree().Simple1D(smoothAmount)
                    .WithAnimation(Base.NewBlendTree().Simple1D(input)
                        .WithAnimation(clipSmoothOutput0, 0)
                        .WithAnimation(clipSmoothOutput1, 1), 0)
                    .WithAnimation(Base.NewBlendTree().Simple1D(output)
                        .WithAnimation(clipSmoothOutput0, 0)
                        .WithAnimation(clipSmoothOutput1, 1), 1);
            return bt;
        }

        /// <summary>
        /// この関数は線形的なスムーズパラメータを提供します。
        /// Stepsize(1.0f) * flametimeを使用して、フレーム時間に依存しない一定なスムーズを提供することができます。
        /// https://vrc.school/docs/Other/Advanced-BlendTrees#382b25efca9e461cb97de8d819bdc057
        /// </summary>
        public static AacFlBlendTreeDirect LinerSmooth(
            AacFlBase Base,
            AacFlFloatParameter StepSize, // recommended 0.05f
            AacFlFloatParameter input,
            AacFlFloatParameter output,
            AacFlLayer layer)
        {
            //layer.OverrideValue(StepSize, 0.05f);
            AacFlFloatParameter one = layer.FloatParameter("one");
            layer.OverrideValue(one, 1.0f);
            AacFlFloatParameter Delta = layer.FloatParameter($"{output.Name}_Delta");

            // AacFlClip DeltaNegative100 = Base.NewClip()
            //     .Animating(a => a.AnimatesAnimator(Delta).WithOneFrame(-100.0f)).NonLooping();
            // AacFlClip Delta100 = Base.NewClip()
            //     .Animating(a => a.AnimatesAnimator(Delta).WithOneFrame(100.0f)).NonLooping();
            // AacFlClip OutputNegative100 = Base.NewClip()
            //     .Animating(a => a.AnimatesAnimator(output).WithOneFrame(-100.0f)).NonLooping();
            // AacFlClip Output100 = Base.NewClip()
            //     .Animating(a => a.AnimatesAnimator(output).WithOneFrame(100.0f)).NonLooping();
            AacFlClip OutputNegative1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(-1.0f)).NonLooping();
            AacFlClip Output0 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(0.0f)).NonLooping();
            AacFlClip Output1 = Base.NewClip()
                .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();

            AacFlBlendTree1D DeltaIsInput = Remap(Base, input, Delta, -100f, 100f, -100f, 100f);
            AacFlBlendTree1D DeltaIsNegativeOutput = Remap(Base, output, Delta, 100f, -100f, -100f, 100f);
            AacFlBlendTree1D OutputIsOutput = Remap(Base, output, output, -100f, 100f, -100f, 100f);
            AacFlBlendTree1D LinearBlend = Base.NewBlendTree().Simple1D(Delta)
                .WithAnimation(OutputNegative1, -0.1f)
                .WithAnimation(Output0, 0f)
                .WithAnimation(Output1, 0.1f);

            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct()
                .WithAnimation(DeltaIsInput, one)
                .WithAnimation(DeltaIsNegativeOutput, one)
                .WithAnimation(OutputIsOutput, one)
                .WithAnimation(LinearBlend, StepSize);
            return bt;
        }

        public static AacFlBlendTreeDirect SepalateFloat(
            AacFlBase Base,
            AacFlFloatParameter input,
            AacFlFloatParameter output0_100,
            AacFlFloatParameter output0_010,
            AacFlFloatParameter output0_001,
            AacFlLayer layer)
        {
            AacFlFloatParameter one = layer.FloatParameter("one");
            layer.OverrideValue(one, 1.0f);

            AacFlFloatParameter inputproxy = layer.FloatParameter($"{input.Name}_SepalateProxy");
            AacFlFloatParameter Offset_0_9 = layer.FloatParameter($"{input.Name}_Offset_0_9");
            AacFlFloatParameter Offset_0_09 = layer.FloatParameter($"{input.Name}_Offset_0_09");
            AacFlFloatParameter Offset_0_009 = layer.FloatParameter($"{input.Name}_Offset_0_009");
            AacFlFloatParameter Division_0_9 = layer.FloatParameter($"{input.Name}_Division_0_9");
            AacFlFloatParameter Division_0_09 = layer.FloatParameter($"{input.Name}_Division_0_09");
            AacFlFloatParameter Division_0_009 = layer.FloatParameter($"{input.Name}_Division_0_009");
            AacFlFloatParameter Division_0_0009 = layer.FloatParameter($"{input.Name}_Division_0_0009");
            AacFlFloatParameter Output_0_9 = layer.FloatParameter($"{input.Name}_Output_0_9");
            AacFlFloatParameter Output_0_09 = layer.FloatParameter($"{input.Name}_Output_0_09");
            AacFlFloatParameter Output_0_009 = layer.FloatParameter($"{input.Name}_Output_0_009");
            AacFlFloatParameter Output_0_0009 = layer.FloatParameter($"{input.Name}_Output_0_0009");
            // AacFlFloatParameter Result_0_9 = layer.FloatParameter($"{input.Name}_Result_0_9");
            // AacFlFloatParameter Result_0_09 = layer.FloatParameter($"{input.Name}_Result_0_09");
            // AacFlFloatParameter Result_0_009 = layer.FloatParameter($"{input.Name}_Result_0_009");

            AacFlBlendTree1D RemapDBT = Remap(Base, input, inputproxy, 0, 1);

            // Offset
            AacFlBlendTree1D BT_Offset_0_9 = Remap(Base, inputproxy, Offset_0_9, -0.49999f, 0.50001f);
            AacFlBlendTree1D BT_Offset_0_09 = Remap(Base, inputproxy, Offset_0_09, -0.044999f, 0.95001f);
            AacFlBlendTree1D BT_Offset_0_009 = Remap(Base, inputproxy, Offset_0_009, -0.0044999f, 0.99501f);
            AacFlBlendTreeDirect Offset = Base.NewBlendTree().Direct()//combine
                .WithAnimation(BT_Offset_0_9
                , one)
                .WithAnimation(BT_Offset_0_09
                , one)
                .WithAnimation(BT_Offset_0_009
                , one);

            // Division
            AacFlBlendTree1D BT_1div9 = Remap(Base, Offset_0_9, Division_0_9, 0f, 1.4013e-45f);
            AacFlBlendTree1D BT_1div9toOutput = Remap(Base, Division_0_9, Output_0_9, 0f, 1f, 0f, 1.401298e-45f);
            AacFlBlendTreeDirect BT_CountLayer_0_9 = Base.NewBlendTree().Direct()
                .WithAnimation(BT_1div9
                , one)
                .WithAnimation(BT_1div9toOutput
                , one);
            AacFlBlendTree1D BT_1div09 = Remap(Base, Offset_0_09, Division_0_09, 0f, 1.4013e-44f);
            AacFlBlendTree1D BT_1div09toOutput = Remap(Base, Division_0_09, Output_0_09, 0f, 1f, 0f, 1.401298e-44f);
            AacFlBlendTreeDirect BT_CountLayer_0_09 = Base.NewBlendTree().Direct()
                .WithAnimation(BT_1div09
                , one)
                .WithAnimation(BT_1div09toOutput
                , one);
            AacFlBlendTree1D BT_1div009 = Remap(Base, Offset_0_009, Division_0_009, 0f, 1.4013e-43f);
            AacFlBlendTree1D BT_1div009toOutput = Remap(Base, Division_0_009, Output_0_009, 0f, 1f, 0f, 1.401298e-43f);
            AacFlBlendTreeDirect BT_CountLayer_0_009 = Base.NewBlendTree().Direct()
                .WithAnimation(BT_1div009
                , one)
                .WithAnimation(BT_1div009toOutput
                , one);
            AacFlBlendTree1D BT_1div0009 = Remap(Base, inputproxy, Division_0_0009, 0f, 1.4013e-42f);
            AacFlBlendTree1D BT_1div0009toOutput = Remap(Base, Division_0_0009, Output_0_0009, 0f, 1f, 0f, 1.401298e-42f);
            AacFlBlendTreeDirect BT_CountLayer_0_0009 = Base.NewBlendTree().Direct()
                .WithAnimation(BT_1div0009
                , one)
                .WithAnimation(BT_1div0009toOutput
                , one);
            AacFlBlendTreeDirect CountLayer = Base.NewBlendTree().Direct() //combine
                .WithAnimation(BT_CountLayer_0_9
                , one)
                .WithAnimation(BT_CountLayer_0_09
                , one)
                .WithAnimation(BT_CountLayer_0_009
                , one)
                .WithAnimation(BT_CountLayer_0_0009
                , one);

            // (Substract)Result
            AacFlBlendTreeDirect BT_Sub_0_09m0_9 = Sub(Base, Output_0_09, Output_0_9, output0_100);
            AacFlBlendTreeDirect BT_Sub_0_009m0_09 = Sub(Base, Output_0_009, Output_0_09, output0_010);
            AacFlBlendTreeDirect BT_Sub_0_0009m0_009 = Sub(Base, Output_0_0009, Output_0_009, output0_001);
            AacFlBlendTreeDirect Substract = Base.NewBlendTree().Direct() //combine
                .WithAnimation(BT_Sub_0_09m0_9
                , one)
                .WithAnimation(BT_Sub_0_009m0_09
                , one)
                .WithAnimation(BT_Sub_0_0009m0_009
                , one);

            AacFlBlendTreeDirect bt = Base.NewBlendTree().Direct() //finalize
                .WithAnimation(RemapDBT
                , one)
                .WithAnimation(Offset
                , one)
                .WithAnimation(CountLayer
                , one)
                .WithAnimation(Substract
                , one);

            return bt;
        }


        public static AacFlClip InverseLessThanOne_AsClip(
            AacFlBase Base,
            AacFlFloatParameter output)
        {
            float fps = 1000;
            AnimationCurve curve = new AnimationCurve();
            for (int i = 1; i <= 240; i++)
            {
                Keyframe key = new Keyframe();
                key.time = (1f / i) * 100;
                key.value = i;
                curve.AddKey(key);
                AnimationUtility.SetKeyRightTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyLeftTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
            }

            AacFlClip inverseClip = Base.NewClip()
                .NonLooping()
                .Animating(clip =>
                clip.AnimatesAnimator(output)
                .WithAnimationCurve(curve));
            inverseClip.Clip
                .frameRate = fps;

            return inverseClip;
        }

        //public static AacFlState InverseLessThanOne_AsState(
        //    AacFlBase Base,
        //    AacFlLayer Layer,
        //    AacFlFloatParameter input,
        //    AacFlFloatParameter output)
        //{
        //    AacFlClip inverseClip = InverseLessThanOne_AsClip(Base, output);
        //    AacFlState inverseClipState = Layer.NewState("InverseLessThanOneState").WithAnimation(inverseClip).WithMotionTime(input);
        //    return inverseClipState;
        //}

        //public static AacFlLayer InverseLessThanOne_AsNewLayer(
        //    AacFlBase Base,
        //    AnimatorController AssetContainer,
        //    AacFlFloatParameter input,
        //    AacFlFloatParameter output)
        //{
        //    AacFlClip inverseClip = InverseLessThanOne_AsClip(Base, output);
        //    AacFlLayer Layer = Base.CreateMainArbitraryControllerLayer(AssetContainer);
        //    AacFlState inverseClipState = Layer.NewState("InverseLessThanOneState").WithAnimation(inverseClip).WithMotionTime(input);
        //    return Layer;
        //}

        public static AacFlLayer InverseLessThanOne_AsNewLayer(
            AacFlBase Base,
            AnimatorController AssetContainer,
            AacFlFloatParameter input,
            AacFlFloatParameter output)
        {
            AacFlClip inverseClip = InverseLessThanOne_AsClip(Base, output);
            AacFlLayer Layer = Base.CreateSupportingArbitraryControllerLayer(AssetContainer, input.Name + "InverceTo" + output.Name);
            AacFlState inverseClipState = Layer.NewState("InverseLessThanOneState").WithAnimation(inverseClip).WithMotionTime(input);
            return Layer;
        }
        public static AacFlClip Sin_AsClip(
            AacFlBase Base,
            AacFlFloatParameter output)
        {
            int fps = 360 * 2;
            AnimationCurve curve = new AnimationCurve();
            for (int i = 1; i <= fps; i++)
            {
                Keyframe key = new Keyframe();
                key.time = 1f / i;
                key.value = Mathf.Sin(Mathf.PI * 2 * i / fps);
                curve.AddKey(key);
                AnimationUtility.SetKeyRightTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyLeftTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
            }

            AacFlClip sinClip = Base.NewClip()
                .NonLooping()
                .Animating(clip =>
                clip.AnimatesAnimator(output)
                .WithAnimationCurve(curve));
            sinClip.Clip
                .frameRate = fps;

            return sinClip;
        }

        public static AacFlClip Cos_AsClip(
            AacFlBase Base,
            AacFlFloatParameter output)
        {
            int fps = 360 * 2;
            AnimationCurve curve = new AnimationCurve();
            for (int i = 1; i <= fps; i++)
            {
                Keyframe key = new Keyframe();
                key.time = 1f / i;
                key.value = Mathf.Cos(Mathf.PI * 2 * i / fps);
                curve.AddKey(key);
                AnimationUtility.SetKeyRightTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyLeftTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
            }

            AacFlClip cosClip = Base.NewClip()
                .NonLooping()
                .Animating(clip =>
                clip.AnimatesAnimator(output)
                .WithAnimationCurve(curve));
            cosClip.Clip
                .frameRate = fps;

            return cosClip;
        }

        public static AacFlClip Tan_AsClip(
            AacFlBase Base,
            AacFlFloatParameter output)
        {
            int fps = 360 * 2;
            AnimationCurve curve = new AnimationCurve();
            for (int i = 1; i <= fps; i++)
            {
                Keyframe key = new Keyframe();
                key.time = 1f / i;
                key.value = Mathf.Tan(Mathf.PI * 2 * i / fps);
                curve.AddKey(key);
                AnimationUtility.SetKeyRightTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyLeftTangentMode(curve, 0, AnimationUtility.TangentMode.Auto);
            }

            AacFlClip tanClip = Base.NewClip()
                .NonLooping()
                .Animating(clip =>
                clip.AnimatesAnimator(output)
                .WithAnimationCurve(curve));
            tanClip.Clip
                .frameRate = fps;

            return tanClip;
        }

        public static AacFlBlendTreeDirect Atan_AsClipFourHalf(
            AacFlBase Base,
            AacFlLayer layer,
            AacFlFloatParameter input,
            AacFlFloatParameter output)
        {
            // input = y / x
            // 基本 x = 1
            // つまり input = y
            AacFlFloatParameter one = layer.FloatParameter("One");
            layer.OverrideValue(one, 1.0f);

            int fps = 1000;
            AnimationCurve curve = new AnimationCurve();
            for (int i = 0; i <= 45; i++)
            {
                Keyframe key = new Keyframe();
                key.time = Mathf.Tan(i * Mathf.Deg2Rad) * 100;
                key.value = (float)i / 360;
                curve.AddKey(key);
            }
            for (int i = 0; i < curve.keys.Length; i++)
            {
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
            }

            AacFlClip atanClip = Base.NewClip()
                .NonLooping()
                .Animating(clip =>
                clip.AnimatesAnimator(output)
                .WithAnimationCurve(curve));
            atanClip.Clip
                .frameRate = fps;

            AacFlBlendTreeDirect bt = Base.NewBlendTree()
                .Direct()
                .WithAnimation(atanClip, one);
            return bt;
        }

        public static AacFlClip Asin_AsClipHalfNegative(
            AacFlBase Base,
            AacFlLayer layer,
            AacFlFloatParameter input,
            AacFlFloatParameter output)
        {
            AacFlFloatParameter one = layer.FloatParameter("One");
            layer.OverrideValue(one, 1.0f);
            int fps = 1000;
            AnimationCurve curve = new AnimationCurve();
            for (int i = 0; i <= 90; i++)
            {
                Keyframe key = new Keyframe();
                key.time = 100f - Mathf.Sin(i * Mathf.Deg2Rad) * 100;
                key.value = (float)i / 360;
                curve.AddKey(key);
            }
            for (int i = 0; i < curve.keys.Length; i++)
            {
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
            }
            AacFlClip asinClip = Base.NewClip()
                .NonLooping()
                .Animating(clip =>
                clip.AnimatesAnimator(output)
                .WithAnimationCurve(curve));
            asinClip.Clip
                .frameRate = fps;
            return asinClip;
        }

        public static AacFlClip Asin_AsClipHalfPositive(
            AacFlBase Base,
            AacFlLayer layer,
            AacFlFloatParameter input,
            AacFlFloatParameter output)
        {
            AacFlFloatParameter one = layer.FloatParameter("One");
            layer.OverrideValue(one, 1.0f);
            int fps = 1000;
            AnimationCurve curve = new AnimationCurve();
            for (int i = 0; i <= 90; i++)
            {
                Keyframe key = new Keyframe();
                key.time = Mathf.Sin(i * Mathf.Deg2Rad) * 100;
                key.value = (float)i / 360;
                curve.AddKey(key);
            }
            for (int i = 0; i < curve.keys.Length; i++)
            {
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
            }
            AacFlClip asinClip = Base.NewClip()
                .NonLooping()
                .Animating(clip =>
                clip.AnimatesAnimator(output)
                .WithAnimationCurve(curve));
            asinClip.Clip
                .frameRate = fps;
            return asinClip;
        }

        public static AacFlBlendTree FloatAsBool(
            AacFlBase Base,
            AacFlFloatParameter input,
            AacFlFloatParameter output
            )
        {
            var zeroClip = Base.NewClip()
                .Animating(a =>
                a.AnimatesAnimator(output)
                .WithOneFrame(0.0f))
                .NonLooping();
            var oneClip = Base.NewClip()
                .Animating(a =>
                a.AnimatesAnimator(output)
                .WithOneFrame(1.0f))
                .NonLooping();
            var boolBlendTree = Base.NewBlendTree()
                .Simple1D(input)
                .WithAnimation(zeroClip, 0)
                .WithAnimation(zeroClip, 1)
                .WithAnimation(oneClip, 1);

            return boolBlendTree;
        }

        public static AacFlBlendTree1D ToggleMotion(
            AacFlBase Base,
            AacFlFloatParameter input,
            AacFlBlendTree blendTreeOff,
            AacFlBlendTree blendTreeOn
            )
        {
            return ToggleMotion(Base, input, blendTreeOff.BlendTree, blendTreeOn.BlendTree);
        }

        public static AacFlBlendTree1D ToggleMotion(
            AacFlBase Base,
            AacFlFloatParameter input,
            AacFlClip clipOff,
            AacFlClip clipOn
            )
        {
            return ToggleMotion(Base, input, clipOff.Clip, clipOn.Clip);
        }

        public static AacFlBlendTree1D ToggleMotion(
            AacFlBase Base,
            AacFlFloatParameter input,
            Motion motionOff,
            Motion motionOn
            )
        {
            return Base.NewBlendTree()
                .Simple1D(input)
                .WithAnimation(motionOff, 0)
                .WithAnimation(motionOn, 1);
        }

        public static AacFlBlendTree1D ToggleObject(
            AacFlBase Base,
            AacFlFloatParameter input,
            GameObject gameObject
            )
        {
            var clipOn = Base.NewClip()
                .Toggling(gameObject, true)
                .NonLooping();
            var clipOff = Base.NewClip()
                .Toggling(gameObject, false)
                .NonLooping();

            return ToggleMotion(Base, input, clipOff, clipOn);
        }

        public static AacFlBlendTree1D ToggleObject(
            AacFlBase Base,
            AacFlFloatParameter input,
            GameObject[] gameObject
            )
        {
            return ToggleObject(Base, input, gameObject, new GameObject[0]);
        }

        public static AacFlBlendTree1D ToggleObject(
            AacFlBase Base,
            AacFlFloatParameter input,
            GameObject[] gameObject,
            GameObject[] inverceObject
            )
        {
            var clipOn = Base.NewClip()
                .Toggling(gameObject, true)
                .Toggling(inverceObject, false)
                .NonLooping();
            var clipOff = Base.NewClip()
                .Toggling(gameObject, false)
                .Toggling(inverceObject, true)
                .NonLooping();

            return ToggleMotion(Base, input, clipOff, clipOn);
        }

        public static AacFlBlendTree1D ToggleComponent(
            AacFlBase Base,
            AacFlFloatParameter input,
            Component component
            )
        {
            var clipOn = Base.NewClip()
                .TogglingComponent(component, true)
                .NonLooping();
            var clipOff = Base.NewClip()
                .TogglingComponent(component, false)
                .NonLooping();
            return ToggleMotion(Base, input, clipOff, clipOn);
        }

        // public static AacFlBlendTree SmoothLinear(
        //     AacFlBase Base,
        //     AacFlFloatParameter smoothAmount,
        //     AacFlFloatParameter input,
        //     AacFlFloatParameter output)
        // {
        //     AacFlClip clipSmoothOutput0 = Base.NewClip("AAPClip_SmoothOutput_0")
        //         .Animating(a => a.AnimatesAnimator(output).WithOneFrame(0.0f)).NonLooping();
        //     AacFlClip clipSmoothOutput1 = Base.NewClip("AAPClip_SmoothOutput_1")
        //         .Animating(a => a.AnimatesAnimator(output).WithOneFrame(1.0f)).NonLooping();

        //     AacFlBlendTree bt = Base.NewBlendTree().Simple1D(smoothAmount)
        //             .WithAnimation(Base.NewBlendTree().Simple1D(input)
        //                 .WithAnimation(clipSmoothOutput0, 0)
        //                 .WithAnimation(clipSmoothOutput1, 1), 0)
        //             .WithAnimation(Base.NewBlendTree().Simple1D(output)
        //                 .WithAnimation(clipSmoothOutput0, 0)
        //                 .WithAnimation(clipSmoothOutput1, 1), 1);
        //     return bt;
        // }
    }
}
#endif
#endif