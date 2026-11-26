// Usage: AgeOfHammer.Game [--watch]
//   --watch  skip the menu and watch an AI vs AI battle with the default armies.
using var game = new AgeOfHammer.Game.HammerGame(args);
game.Run();
