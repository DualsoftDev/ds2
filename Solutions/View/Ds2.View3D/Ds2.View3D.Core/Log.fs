module Ds2.View3D.Log

open log4net

let private logger = LogManager.GetLogger("Ds2.View3D")

let info fmt = Printf.ksprintf (fun msg -> logger.Info(msg)) fmt
let warn fmt = Printf.ksprintf (fun msg -> logger.Warn(msg)) fmt
let error fmt = Printf.ksprintf (fun msg -> logger.Error(msg)) fmt
let debug fmt = Printf.ksprintf (fun msg -> logger.Debug(msg)) fmt
