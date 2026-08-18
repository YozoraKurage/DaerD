// The public surface of Yozolab.DaerD.Editor is the recipe API, and nothing else is public.
//
// A recipe compiles in the user's own assembly, so "public" here is not a C# habit — it is a
// promise to a person writing C# against DaerD, and the only promise this package makes about
// its types. Everything else is internal, including the parts a reader could mistake for API:
// the window, the controller IR, the generation engine, the saved records. A type that leaves
// this list takes a recipe with it, loudly, at compile time.
//
// The promise (ADR 0016's "the Recipe API does not change" covers exactly this list):
//
//   Recipes             ControllerRecipe, ControllerBuilder
//   Layers and machines LayerBuilder, SyncedLayerBuilder, MachineBuilder, MachineScope,
//                       StateBuilder, TransitionBuilder, Condition
//   Blend trees         TreeBuilder, TreeChildBuilder
//   Parameters          ParamHandle, BoolParam, IntParam, FloatParam, TriggerParam
//   Gadgets             GadgetRecipeBuilder, ParamRef, ObjectRecipeBuilder,
//                       ObjectToggleBuilder, AsyncSyncRecipeBuilder
//   Export from code    RecipeExport (with its nested Field, Source, Options and Written),
//                       RecipeExportCli — the batch-job entry point, public because it is
//                       reached by name from outside through -executeMethod
//
// All of them live in Yozolab.DaerD.Authoring. Two tests keep this comment honest rather than
// decorative: RecipeCompileTests compiles both halves of a real export against the built DLL
// from outside, and the census test beside it fails when the assembly exports anything this
// list does not name.
//
// Tests reach past all of it through the InternalsVisibleTo below, so nothing is left public
// for their sake.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Yozolab.DaerD.Tests")]
